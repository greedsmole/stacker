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
    public sealed class YamlStack
    {
        public string? Id
        {
            get; set;
        }
        public string? Name
        {
            get; set;
        }
        public string? Base
        {
            get; set;
        }
        public List<string>? Branches
        {
            get; set;
        }
    }
}
