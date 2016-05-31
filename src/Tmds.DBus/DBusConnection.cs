// Copyright 2016 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Tmds.DBus
{
    class DBusConnection : IDBusConnection
    {
        private struct PendingSend
        {
            public Message Message;
            public CancellationTokenRegistration CancellationRegistration;
            public TaskCompletionSource<bool> CompletionSource;
            public CancellationToken CancellationToken;
        }

        private class SignalHandlerRegistration : IDisposable
        {
            public SignalHandlerRegistration(DBusConnection dbusConnection, SignalMatchRule rule, SignalHandler handler)
            {
                _connection = dbusConnection;
                _rule = rule;
                _handler = handler;
            }

            public void Dispose()
            {
                _connection.RemoveSignalHandler(_rule, _handler);
            }

            private DBusConnection _connection;
            private SignalMatchRule _rule;
            private SignalHandler _handler;
        }

        private class NameOwnerWatcherRegistration : IDisposable
        {
            public NameOwnerWatcherRegistration(DBusConnection dbusConnection, OwnerChangedMatchRule rule, Action<ServiceOwnerChangedEventArgs> handler)
            {
                _connection = dbusConnection;
                _rule = rule;
                _handler = handler;
            }

            public void Dispose()
            {
                _connection.RemoveNameOwnerWatcher(_rule, _handler);
            }

            private DBusConnection _connection;
            private OwnerChangedMatchRule _rule;
            private Action<ServiceOwnerChangedEventArgs> _handler;
        }

        private class ServiceNameRegistration
        {
            public Action OnAquire;
            public Action OnLost;
            public SynchronizationContext SynchronizationContext;
        }

        private enum State
        {
            Created,
            Connecting,
            Connected,
            Disconnected,
            Disposed
        }

        public static readonly ObjectPath DBusObjectPath = new ObjectPath("/org/freedesktop/DBus");
        public const string DBusServiceName = "org.freedesktop.DBus";
        public const string DBusInterface = "org.freedesktop.DBus";

        public static async Task<DBusConnection> OpenAsync(string address, Action<Exception> onDisconnect, CancellationToken cancellationToken)
        {
            var _entries = AddressEntry.ParseEntries(address);
            if (_entries.Length == 0)
            {
                throw new ArgumentException("No addresses were found", nameof(address));
            }

            Guid _serverId = Guid.Empty;
            IMessageStream stream = null;
            var index = 0;
            while (index < _entries.Length)
            {
                AddressEntry entry = _entries[index++];

                _serverId = entry.Guid;
                try
                {
                    stream = await MessageStream.OpenAsync(entry, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    if (index < _entries.Length)
                        continue;
                    throw;
                }

                break;
            }

            var dbusConnection = new DBusConnection(stream);
            await dbusConnection.ConnectAsync(onDisconnect, cancellationToken);
            return dbusConnection;
        }

        private readonly IMessageStream _stream;
        private readonly object _gate = new object();
        private readonly Dictionary<SignalMatchRule, SignalHandler> _signalHandlers = new Dictionary<SignalMatchRule, SignalHandler>();
        private readonly Dictionary<string, Action<ServiceOwnerChangedEventArgs>> _nameOwnerWatchers = new Dictionary<string, Action<ServiceOwnerChangedEventArgs>>();
        private Dictionary<uint, TaskCompletionSource<Message>> _pendingMethods = new Dictionary<uint, TaskCompletionSource<Message>>();
        private readonly Dictionary<ObjectPath, MethodHandler> _methodHandlers = new Dictionary<ObjectPath, MethodHandler>();
        private readonly Dictionary<string, ServiceNameRegistration> _serviceNameRegistrations = new Dictionary<string, ServiceNameRegistration>();

        private State _state = State.Created;
        private string _localName;
        private bool? _remoteIsBus;
        private Action<Exception> _onDisconnect;
        private Exception _disconnectReason;
        private int _methodSerial;
        private ConcurrentQueue<PendingSend> _sendQueue;
        private SemaphoreSlim _sendSemaphore;

        public string LocalName => _localName;
        public bool? RemoteIsBus => _remoteIsBus;

        internal DBusConnection(IMessageStream stream)
        {
            _stream = stream;
            _sendQueue = new ConcurrentQueue<PendingSend>();
            _sendSemaphore = new SemaphoreSlim(1);
        }

        public async Task ConnectAsync(Action<Exception> onDisconnect = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            lock (_gate)
            {
                if (_state != State.Created)
                {
                    throw new InvalidOperationException("Unable to connect");
                }
                _state = State.Connecting;
            }

            ReceiveMessages();

            _localName = await CallHelloAsync(cancellationToken);
            _remoteIsBus = !string.IsNullOrEmpty(_localName);

            lock (_gate)
            {
                if (_state == State.Connecting)
                {
                    _state = State.Connected;
                }
                ThrowIfNotConnected();

                _onDisconnect = onDisconnect;
            }
        }

        public void Dispose()
        {
            DoDisconnect(State.Disposed, null);
        }

        public Task<Message> CallMethodAsync(Message msg, CancellationToken cancellationToken)
        {
            return CallMethodAsync(msg, cancellationToken, checkConnected: true, checkReplyType: true);
        }

        public void EmitSignal(Message message)
        {
            message.Header.Serial = GenerateSerial();
            SendMessageAsync(message, CancellationToken.None);
        }

        public void AddMethodHandler(ObjectPath path, MethodHandler handler)
        {
            lock (_gate)
            {
                _methodHandlers.Add(path, handler);
            }
        }

        public void RemoveMethodHandler(ObjectPath path)
        {
            lock (_gate)
            {
                _methodHandlers.Remove(path);
            }
        }

        public async Task<IDisposable> WatchSignalAsync(ObjectPath path, string @interface, string signalName, SignalHandler handler, CancellationToken cancellationToken)
        {
            SignalMatchRule rule = new SignalMatchRule()
            {
                Interface = @interface,
                Member = signalName,
                Path = path
            };

            Task task = null;
            lock (_gate)
            {
                ThrowIfNotConnected();
                if (_signalHandlers.ContainsKey(rule))
                {
                    _signalHandlers[rule] = (SignalHandler)Delegate.Combine(_signalHandlers[rule], handler);
                    task = Task.CompletedTask;
                }
                else
                {
                    _signalHandlers[rule] = handler;
                    if (_remoteIsBus == true)
                    {
                        task = CallAddMatchRuleAsync(rule.ToString(), cancellationToken);
                    }
                }
            }
            SignalHandlerRegistration registration = new SignalHandlerRegistration(this, rule, handler);
            try
            {
                if (task != null)
                {
                    await task;
                }
            }
            catch
            {
                registration.Dispose();
                throw;
            }
            return registration;
        }

        public async Task<RequestNameReply> RequestNameAsync(string name, RequestNameOptions options, Action onAquired, Action onLost, SynchronizationContext synchronzationContext, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ThrowIfNotConnected();
                ThrowIfRemoteIsNotBus();

                if (_serviceNameRegistrations.ContainsKey(name))
                {
                    throw new InvalidOperationException("The name is already requested");
                }
                _serviceNameRegistrations[name] = new ServiceNameRegistration
                {
                    OnAquire = onAquired,
                    OnLost = onLost,
                    SynchronizationContext = synchronzationContext
                };
            }
            try
            {
                var reply = await CallRequestNameAsync(name, options, cancellationToken);
                return reply;
            }
            catch
            {
                lock (_gate)
                {
                    _serviceNameRegistrations.Remove(name);
                }
                throw;
            }
        }

        public Task<ReleaseNameReply> ReleaseNameAsync(string name, CancellationToken cancellationToken = default(CancellationToken))
        {
            lock (_gate)
            {
                ThrowIfRemoteIsNotBus();

                if (!_serviceNameRegistrations.ContainsKey(name))
                {
                    return Task.FromResult(ReleaseNameReply.NotOwner);
                }
                _serviceNameRegistrations.Remove(name);

                ThrowIfNotConnected();
            }
            return CallReleaseNameAsync(name, cancellationToken);
        }

        public async Task<IDisposable> WatchNameOwnerChangedAsync(string serviceName, Action<ServiceOwnerChangedEventArgs> handler, CancellationToken cancellationToken = default(CancellationToken))
        {
            var rule = new OwnerChangedMatchRule(serviceName);

            Task task = null;
            lock (_gate)
            {
                ThrowIfNotConnected();
                ThrowIfRemoteIsNotBus();

                if (_nameOwnerWatchers.ContainsKey(serviceName))
                {
                    _nameOwnerWatchers[serviceName] = (Action<ServiceOwnerChangedEventArgs>)Delegate.Combine(_nameOwnerWatchers[serviceName], handler);
                    task = Task.CompletedTask;
                }
                else
                {
                    _nameOwnerWatchers[serviceName] = handler;
                    task = CallAddMatchRuleAsync(rule.ToString(), cancellationToken);
                }
            }
            NameOwnerWatcherRegistration registration = new NameOwnerWatcherRegistration(this, rule, handler);
            try
            {
                await task;
            }
            catch
            {
                registration.Dispose();
                throw;
            }
            return registration;
        }

        private void ThrowIfRemoteIsNotBus()
        {
            if (RemoteIsBus != true)
            {
                throw new InvalidOperationException("The remote peer is not a bus");
            }
        }

        private async void SendPendingMessages()
        {
            try
            {
                await _sendSemaphore.WaitAsync();
                PendingSend pendingSend;
                while (_sendQueue.TryDequeue(out pendingSend))
                {
                    pendingSend.CancellationRegistration.Dispose();
                    if (pendingSend.CancellationToken.IsCancellationRequested)
                    {
                        pendingSend.CompletionSource.TrySetCanceled();
                    }
                    else
                    {
                        try
                        {
                            await _stream.SendMessageAsync(pendingSend.Message, CancellationToken.None);
                            pendingSend.CompletionSource.SetResult(true);
                        }
                        catch (System.Exception e)
                        {
                            pendingSend.CompletionSource.SetException(e);
                        }
                    }
                }
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }

        private Task SendMessageAsync(Message message, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            var ctRegistration = cancellationToken.Register(() => tcs.SetCanceled());
            var pendingSend = new PendingSend()
            {
                Message = message,
                CompletionSource = tcs,
                CancellationRegistration = ctRegistration,
                CancellationToken = cancellationToken
            };
            _sendQueue.Enqueue(pendingSend);
            SendPendingMessages();
            return tcs.Task;
        }

        private async void ReceiveMessages()
        {
            try
            {
                while (true)
                {
                    Message msg = await _stream.ReceiveMessageAsync(CancellationToken.None);
                    if (msg == null)
                    {
                        throw new IOException("Connection closed by peer");
                    }
                    HandleMessage(msg);
                }
            }
            catch (Exception e)
            {
                DoDisconnect(State.Disposed, e);
            }
        }

        private void HandleMessage(Message msg)
        {
            uint? serial = msg.Header.ReplySerial;
            if (serial != null)
            {
                uint serialValue = (uint)serial;
                TaskCompletionSource<Message> pending = null;
                lock (_gate)
                {
                    if (_pendingMethods?.TryGetValue(serialValue, out pending) == true)
                    {
                        _pendingMethods.Remove(serialValue);
                    }
                }
                if (pending != null)
                {
                    pending.SetResult(msg);
                }
                else
                {
                    pending.SetException(new ProtocolException("Unexpected reply message received: MessageType = '" + msg.Header.MessageType + "', ReplySerial = " + serialValue));
                }
                return;
            }

            switch (msg.Header.MessageType)
            {
                case MessageType.MethodCall:
                    HandleMethodCall(msg);
                    break;
                case MessageType.Signal:
                    HandleSignal(msg);
                    break;
                case MessageType.Error:
                    string errMsg = String.Empty;
                    if (msg.Header.Signature.Value.Value.StartsWith("s"))
                    {
                        MessageReader reader = new MessageReader(msg, null);
                        errMsg = reader.ReadString();
                    }
                    throw new DBusException(msg.Header.ErrorName, errMsg);
                case MessageType.Invalid:
                default:
                    throw new ProtocolException("Invalid message received: MessageType='" + msg.Header.MessageType + "'");
            }
        }

        private void HandleSignal(Message msg)
        {
            switch (msg.Header.Interface)
            {
                case "org.freedesktop.DBus":
                    switch (msg.Header.Member)
                    {
                        case "NameAcquired":
                        case "NameLost":
                        {
                            MessageReader reader = new MessageReader(msg, null);
                            var name = reader.ReadString();
                            bool aquiredNotLost = msg.Header.Member == "NameAcquired";
                            OnNameAcquiredOrLost(name, aquiredNotLost);
                            return;
                        }
                        case "NameOwnerChanged":
                        {
                            MessageReader reader = new MessageReader(msg, null);
                            var serviceName = reader.ReadString();
                            var oldOwner = reader.ReadString();
                            oldOwner = string.IsNullOrEmpty(oldOwner) ? null : oldOwner;
                            var newOwner = reader.ReadString();
                            newOwner = string.IsNullOrEmpty(newOwner) ? null : newOwner;
                            Action<ServiceOwnerChangedEventArgs> watchers = null;
                            lock (_gate)
                            {
                                _nameOwnerWatchers.TryGetValue(serviceName, out watchers);
                            }
                            watchers?.Invoke(new ServiceOwnerChangedEventArgs(serviceName, oldOwner, newOwner));
                            return;
                        }
                        default:
                            break;
                    }
                    break;
                default:
                    break;
            }

            SignalMatchRule rule = new SignalMatchRule()
            {
                Interface = msg.Header.Interface,
                Member = msg.Header.Member,
                Path = msg.Header.Path.Value
            };

            SignalHandler signalHandler;
            lock (_gate)
            {
                if (_signalHandlers.TryGetValue(rule, out signalHandler) && signalHandler != null)
                {
                    try
                    {
                        signalHandler(msg);
                    }
                    catch (Exception e)
                    {
                        throw new InvalidOperationException("Signal handler for " + msg.Header.Interface + "." + msg.Header.Member + " threw an exception", e);
                    }
                }
            }
        }

        private void OnNameAcquiredOrLost(string name, bool aquiredNotLost)
        {
            Action action = null;
            SynchronizationContext synchronizationContext = null;
            lock (_gate)
            {
                ServiceNameRegistration registration;
                if (_serviceNameRegistrations.TryGetValue(name, out registration))
                {
                    action = aquiredNotLost ? registration.OnAquire : registration.OnLost;
                    synchronizationContext = registration.SynchronizationContext;
                }
            }
            if (action != null)
            {
                if (synchronizationContext != null)
                {
                    synchronizationContext.Post(_ => action(), null);
                }
                else
                {
                    action();
                }
            }
        }

        private void DoDisconnect(State nextState, Exception disconnectReason)
        {
            Dictionary<uint, TaskCompletionSource<Message>> pendingMethods = null;
            lock (_gate)
            {
                if ((_state == State.Disconnected) || (_state == State.Disposed))
                {
                    if (nextState == State.Disposed)
                    {
                        _state = nextState;
                    }
                    return;
                }

                _state = nextState;
                _stream.Dispose();
                _disconnectReason = disconnectReason;
                pendingMethods = _pendingMethods;
                _pendingMethods = null;
                _signalHandlers.Clear();
                _nameOwnerWatchers.Clear();
                _serviceNameRegistrations.Clear();
            }

            foreach (var tcs in pendingMethods.Values)
            {
                if (disconnectReason != null)
                {
                    tcs.SetException(new DisconnectedException(disconnectReason));
                }
                else
                {
                    tcs.SetException(new ObjectDisposedException(typeof(Connection).FullName));
                }
            }
            if (_onDisconnect != null)
            {
                _onDisconnect(disconnectReason);
            }
        }

        private void SendMessage(Message message)
        {
            if (message.Header.Serial == 0)
            {
                message.Header.Serial = GenerateSerial();
            }
            SendMessageAsync(message, CancellationToken.None);
        }

        private async void HandleMethodCall(Message methodCall)
        {
            switch (methodCall.Header.Interface)
            {
                case "org.freedesktop.DBus.Peer":
                    switch (methodCall.Header.Member)
                    {
                        case "Ping":
                        {
                            SendMessage(MessageHelper.ConstructReply(methodCall));
                            return;
                        }
                        case "GetMachineId":
                        {
                            SendMessage(MessageHelper.ConstructReply(methodCall, Environment.MachineId));
                            return;
                        }
                    }
                    break;
            }

            MethodHandler methodHandler;
            if (_methodHandlers.TryGetValue(methodCall.Header.Path.Value, out methodHandler))
            {
                var reply = await methodHandler(methodCall, CancellationToken.None);
                reply.Header.ReplySerial = methodCall.Header.Serial;
                reply.Header.Destination = methodCall.Header.Sender;
                SendMessage(reply);
            }
            else
            {
                SendUnknownMethodError(methodCall);
            }
        }

        private async Task<string> CallHelloAsync(CancellationToken cancellationToken)
        {
            Message callMsg = new Message()
            {
                Header = new Header(MessageType.MethodCall)
                {
                    Path = DBusObjectPath,
                    Interface = DBusInterface,
                    Member = "Hello",
                    Destination = DBusServiceName
                }
            };

            Message reply = await CallMethodAsync(callMsg, cancellationToken, checkConnected: false, checkReplyType: false);

            if (reply.Header.MessageType == MessageType.Error)
            {
                return string.Empty;
            }
            else if (reply.Header.MessageType == MessageType.MethodReturn)
            {
                var reader = new MessageReader(reply, null);
                return reader.ReadString();
            }
            else
            {
                throw new ProtocolException("Got unexpected message of type " + reply.Header.MessageType + " while waiting for a MethodReturn or Error");
            }
        }

        private async Task<RequestNameReply> CallRequestNameAsync(string name, RequestNameOptions options, CancellationToken cancellationToken)
        {
            var writer = new MessageWriter();
            writer.WriteString(name);
            writer.WriteUInt32((uint)options);

            Message callMsg = new Message()
            {
                Header = new Header(MessageType.MethodCall)
                {
                    Path = DBusObjectPath,
                    Interface = DBusInterface,
                    Member = "RequestName",
                    Destination = DBusServiceName,
                    Signature = "su"
                },
                Body = writer.ToArray()
            };

            Message reply = await CallMethodAsync(callMsg, cancellationToken, checkConnected: true, checkReplyType: true);

            var reader = new MessageReader(reply, null);
            var rv = reader.ReadUInt32();
            return (RequestNameReply)rv;
        }

        private async Task<ReleaseNameReply> CallReleaseNameAsync(string name, CancellationToken cancellationToken)
        {
            var writer = new MessageWriter();
            writer.WriteString(name);

            Message callMsg = new Message()
            {
                Header = new Header(MessageType.MethodCall)
                {
                    Path = DBusObjectPath,
                    Interface = DBusInterface,
                    Member = "ReleaseName",
                    Destination = DBusServiceName,
                    Signature = Signature.StringSig
                },
                Body = writer.ToArray()
            };

            Message reply = await CallMethodAsync(callMsg, cancellationToken, checkConnected: true, checkReplyType: true);

            var reader = new MessageReader(reply, null);
            var rv = reader.ReadUInt32();
            return (ReleaseNameReply)rv;
        }

        private void SendUnknownMethodError(Message callMessage)
        {
            if (!callMessage.Header.ReplyExpected)
            {
                return;
            }

            string errMsg = String.Format("Method \"{0}\" with signature \"{1}\" on interface \"{2}\" doesn't exist",
                                           callMessage.Header.Member,
                                           callMessage.Header.Signature?.Value,
                                           callMessage.Header.Interface);

            SendErrorReply(callMessage, "org.freedesktop.DBus.Error.UnknownMethod", errMsg);
        }

        private void SendErrorReply(Message incoming, string errorName, string errorMessage)
        {
            SendMessage(MessageHelper.ConstructErrorReply(incoming, errorName, errorMessage));
        }

        private uint GenerateSerial()
        {
            return (uint)Interlocked.Increment(ref _methodSerial);
        }

        private async Task<Message> CallMethodAsync(Message msg, CancellationToken cancellationToken, bool checkConnected, bool checkReplyType)
        {
            msg.Header.ReplyExpected = true;
            var serial = GenerateSerial();
            msg.Header.Serial = serial;

            TaskCompletionSource<Message> pending = new TaskCompletionSource<Message>();
            lock (_gate)
            {
                if (checkConnected)
                {
                    ThrowIfNotConnected();
                }
                else
                {
                    ThrowIfNotConnecting();
                }
                _pendingMethods[msg.Header.Serial] = pending;
            }

            try
            {
                await SendMessageAsync(msg, cancellationToken);
            }
            catch
            {
                lock (_gate)
                {
                    _pendingMethods?.Remove(serial);
                }
                throw;
            }

            var reply = await pending.Task;

            if (checkReplyType)
            {
                switch (reply.Header.MessageType)
                {
                    case MessageType.MethodReturn:
                        return reply;
                    case MessageType.Error:
                        string errorMessage = String.Empty;
                        if (reply.Header.Signature?.Value?.StartsWith("s") == true)
                        {
                            MessageReader reader = new MessageReader(reply, null);
                            errorMessage = reader.ReadString();
                        }
                        throw new DBusException(reply.Header.ErrorName, errorMessage);
                    default:
                        throw new ProtocolException("Got unexpected message of type " + reply.Header.MessageType + " while waiting for a MethodReturn or Error");
                }
            }

            return reply;
        }

        private void RemoveSignalHandler(SignalMatchRule rule, SignalHandler dlg)
        {
            lock (_gate)
            {
                if (_signalHandlers.ContainsKey(rule))
                {
                    _signalHandlers[rule] = (SignalHandler)Delegate.Remove(_signalHandlers[rule], dlg);
                    if (_signalHandlers[rule] == null)
                    {
                        _signalHandlers.Remove(rule);
                        if (_remoteIsBus == true)
                        {
                            CallRemoveMatchRule(rule.ToString());
                        }
                    }
                }
            }
        }

        private void RemoveNameOwnerWatcher(OwnerChangedMatchRule rule, Action<ServiceOwnerChangedEventArgs> dlg)
        {
            lock (_gate)
            {
                if (_nameOwnerWatchers.ContainsKey(rule.ServiceName))
                {
                    _nameOwnerWatchers[rule.ServiceName] = (Action<ServiceOwnerChangedEventArgs>)Delegate.Remove(_nameOwnerWatchers[rule.ServiceName], dlg);
                    if (_nameOwnerWatchers[rule.ServiceName] == null)
                    {
                        _nameOwnerWatchers.Remove(rule.ServiceName);
                        CallRemoveMatchRule(rule.ToString());
                    }
                }
            }
        }

        private void CallRemoveMatchRule(string rule)
        {
            var reply = CallMethodAsync(DBusServiceName, DBusObjectPath, DBusInterface, "RemoveMatch", rule, CancellationToken.None);
        }

        private Task CallAddMatchRuleAsync(string rule, CancellationToken cancellationToken)
        {
            return CallMethodAsync(DBusServiceName, DBusObjectPath, DBusInterface, "AddMatch", rule, cancellationToken);
        }

        private Task CallMethodAsync(string destination, ObjectPath objectPath, string @interface, string method, string arg, CancellationToken cancellationToken)
        {
            var header = new Header(MessageType.MethodCall)
            {
                Path = objectPath,
                Interface = @interface,
                Member = @method,
                Signature = Signature.StringSig,
                Destination = destination
            };
            var writer = new MessageWriter();
            writer.WriteString(arg);
            var message = new Message()
            {
                Header = header,
                Body = writer.ToArray()
            };
            return CallMethodAsync(message, cancellationToken);
        }

        private void ThrowIfNotConnected()
        {
            if (_state == State.Disconnected)
            {
                throw new DisconnectedException(_disconnectReason);
            }
            else if (_state == State.Created)
            {
                throw new InvalidOperationException("Not Connected");
            }
            else if (_state == State.Connecting)
            {
                throw new InvalidOperationException("Connecting");
            }
            else if (_state == State.Disposed)
            {
                throw new ObjectDisposedException(typeof(Connection).FullName);
            }
        }

        private void ThrowIfNotConnecting()
        {
            if (_state == State.Disconnected)
            {
                throw new DisconnectedException(_disconnectReason);
            }
            else if (_state == State.Created)
            {
                throw new InvalidOperationException("Not Connected");
            }
            else if (_state == State.Connected)
            {
                throw new InvalidOperationException("Already Connected");
            }
            else if (_state == State.Disposed)
            {
                throw new ObjectDisposedException(typeof(Connection).FullName);
            }
        }
    }
}