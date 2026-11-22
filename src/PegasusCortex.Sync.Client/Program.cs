// The executable is intentionally transport-neutral. Pegasus Nexus supplies the authenticated
// transport and local producer composition in deployment; standalone Cortex must not invent
// credentials or directly contact a service from configuration alone.
if (args.Length == 1 && args[0] is "sync-now" or "watch")
{
    Console.Error.WriteLine(
        "sync_transport_adapter_required: run this command through the Nexus-authenticated sync-client host composition.");
    return 3;
}

Console.Error.WriteLine("Usage: PegasusCortex.Sync.Client sync-now|watch");
return 2;
