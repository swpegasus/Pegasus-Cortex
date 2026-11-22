using System.Runtime.CompilerServices;

// Only the existing admitted owner composition calls the internal maintenance
// entry points. Wire callers still pass through its real admission/identity gate.
[assembly: InternalsVisibleTo("PegasusCortex.Mcp.Local")]
[assembly: InternalsVisibleTo("PegasusCortex.Mcp.Local.Tests")]
