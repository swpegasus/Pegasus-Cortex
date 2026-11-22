using System.Runtime.CompilerServices;

// The lexical filesystem boundary is intentionally reachable only from the focused test host.
[assembly: InternalsVisibleTo("PegasusCortex.OfflineJournal.Tests")]
