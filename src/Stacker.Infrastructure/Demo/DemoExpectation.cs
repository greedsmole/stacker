using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

public sealed record DemoExpectation(string Stack, DiffMode Mode, int[] Positions, int Files, int Additions, int Deletions);
