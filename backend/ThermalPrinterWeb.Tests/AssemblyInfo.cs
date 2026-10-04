// The tests run in sequence. Two causes, each one enough (#73):
// 1. Microsoft.Extensions.AI.Abstractions 10.5.2 (it comes with ModelContextProtocol 1.4.0) finds the injected tool parameters
//    by ParameterInfo reference. Two hosts that create the same tool at the same time can get different ParameterInfo objects
//    from the first GetParameters() calls, and then the tool schema lists IPrinterService and PrintJobLog as arguments.
//    Report: https://github.com/dotnet/extensions/issues/7675, fix: https://github.com/dotnet/extensions/pull/7677 (in 10.9.0).
// 2. ImageBlockHandler.SharedQueue is one queue for the process: the test that fills it makes each image test that runs
//    at the same time answer "busy".
[assembly: CollectionBehavior(DisableTestParallelization = true)]
