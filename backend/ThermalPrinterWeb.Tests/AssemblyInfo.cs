// Sequential on purpose, for two causes (#73); each one alone needs it.
// 1. Two hosts that create the same MCP tool at the same time can list IPrinterService and PrintJobLog as tool arguments:
//    Microsoft.Extensions.AI.Abstractions below 10.9.0, https://github.com/dotnet/extensions/issues/7675.
// 2. ImageBlockHandler.SharedQueue is one queue for the process: the test that fills it makes other image tests answer "busy".
[assembly: CollectionBehavior(DisableTestParallelization = true)]
