using Pegasus.Cortex.LearningExportCli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await LearningExportCliApplication.RunAsync(
    args,
    Console.Out,
    cancellation.Token);
