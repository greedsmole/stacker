using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure.Framework;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Stacker.Infrastructure;

public sealed partial class YamlStackStore
{
    public sealed class YamlDocument
    {
        public int Version
        {
            get; set;
        }
        public List<YamlStack>? Stacks
        {
            get; set;
        }
    }
}
