using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.CommandLineUtils;

namespace Tmds.DBus.Tool
{
    class CodeGenCommand : Command
    {
        CommandOption _serviceOption;
        CommandOption _addressOption;
        CommandOption _pathOption;
        CommandOption _norecurseOption;
        CommandOption _namespaceOption;
        CommandOption _outputOption;
        CommandOption _skipOptions;
        CommandOption _interfaceOptions;
        public CodeGenCommand()
        {
            Name = "codegen";
        }
        public override void Configure(CommandLineApplication command)
        {
            _serviceOption = command.Option("--service", "DBus service", CommandOptionType.SingleValue);
            _addressOption = command.Option("--daemon", "Address of DBus daemon. 'session'/'system'/<address> (default: session)", CommandOptionType.SingleValue);
            _pathOption = command.Option("--path", "DBus object path (default: /)", CommandOptionType.SingleValue);
            _norecurseOption = command.Option("--no-recurse", "Don't visit child nodes of path", CommandOptionType.NoValue);
            _namespaceOption = command.Option("--namespace", "C# namespace (default: <service>)", CommandOptionType.SingleValue);
            _outputOption = command.Option("--output", "File to write (default: <namespace>.cs)", CommandOptionType.SingleValue);
            _skipOptions = command.Option("--skip", "DBus interfaces to skip", CommandOptionType.MultipleValue);
            _interfaceOptions = command.Option("--interface", "DBus interfaces to include, optionally specify a name (e.g. 'org.freedesktop.NetworkManager.Device.Wired:WiredDevice')", CommandOptionType.MultipleValue);
        }

        public override void Execute()
        {
            if (!_serviceOption.HasValue())
            {
                throw new ArgumentException("Service argument is required.", "service");
            }
            IEnumerable<string> skipInterfaces = new [] { "org.freedesktop.DBus.Introspectable", "org.freedesktop.DBus.Peer", "org.freedesktop.DBus.ObjectManager", "org.freedesktop.DBus.Properties" };
            if (_skipOptions.HasValue())
            {
                skipInterfaces = skipInterfaces.Concat(_skipOptions.Values);
            }
            Dictionary<string, string> interfaces = null;
            if (_interfaceOptions.HasValue())
            {
                interfaces = new Dictionary<string, string>();
                foreach (var interf in _interfaceOptions.Values)
                {
                    if (interf.Contains(':'))
                    {
                        var split = interf.Split(new[] { ':' });
                        interfaces.Add(split[0], split[1]);
                    }
                    else
                    {
                        interfaces.Add(interf, null);
                    }
                }
            }
            var address = Address.Session;
            if (_addressOption.HasValue())
            {
                if (_addressOption.Value() == "system")
                {
                    address = Address.System;
                }
                else if (_addressOption.Value() == "session")
                {
                    address = Address.Session;
                }
                else
                {
                    address = _addressOption.Value();
                }
            }
            var service = _serviceOption.Value();
            var serviceSplit = service.Split(new [] { '.' });
            var ns = _namespaceOption.Value() ?? $"{serviceSplit[serviceSplit.Length - 1]}.DBus";
            var codeGenArguments = new CodeGenArguments
            {
                Namespace = ns,
                Service = service,
                Path = _pathOption.HasValue() ? _pathOption.Value() : "/",
                Address = address,
                Recurse = !_norecurseOption.HasValue(),
                OutputFileName = _outputOption.Value() ?? $"{ns}.cs",
                SkipInterfaces = skipInterfaces,
                Interfaces = interfaces
            };
            GenerateCodeAsync(codeGenArguments).Wait();
            Console.WriteLine($"Generated: {Path.GetFullPath(codeGenArguments.OutputFileName)}");
        }

        private async static Task GenerateCodeAsync(CodeGenArguments codeGenArguments)
        {
            var fetcher = new IntrospectionsFetcher(
                new IntrospectionsFetcherSettings {
                    Service = codeGenArguments.Service,
                    Path = codeGenArguments.Path,
                    Address = codeGenArguments.Address,
                    Recurse = codeGenArguments.Recurse,
                    SkipInterfaces = codeGenArguments.SkipInterfaces,
                    Interfaces = codeGenArguments.Interfaces
                });
            var introspections = await fetcher.GetIntrospectionsAsync();

            var generator = new Generator(
                new GeneratorSettings {
                    Namespace = codeGenArguments.Namespace
                });
            var code = generator.Generate(introspections);

            File.WriteAllText(codeGenArguments.OutputFileName, code);
        }

        class CodeGenArguments
        {
            public string Namespace { get; set; }
            public string Service { get; set; }
            public string Path { get; set; }
            public string Address { get; set; }
            public bool Recurse { get; set; }
            public string OutputFileName { get; set; }
            public IEnumerable<string> SkipInterfaces { get; set; }
            public Dictionary<string, string> Interfaces { get; set; }
        }
    }
}