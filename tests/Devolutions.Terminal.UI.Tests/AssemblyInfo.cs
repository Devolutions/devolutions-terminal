using Xunit;

// Headless Avalonia shares a dispatcher across test classes; concurrent sessions can initialize it on different threads.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
