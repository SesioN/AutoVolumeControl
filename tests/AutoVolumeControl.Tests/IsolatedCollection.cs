using Xunit;

namespace AutoVolumeControl.Tests
{
    /// <summary>
    /// Tests that measure process-wide state (handle counts) or use the real audio stack. xUnit runs this
    /// collection on its own, after all parallel tests, so other tests cannot skew the measurements.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class IsolatedCollection
    {
        public const string Name = "Isolated";
    }
}
