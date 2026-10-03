// Two WebApplicationFactory hosts that start at the same time disturb each other:
// a tool schema then lists the injected IPrinterService as an argument. One process
// holds one host in production. The suite runs in under a second, so run it in sequence.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
