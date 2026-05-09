using Parsek;

namespace Parsek.Tests.Harness
{
    /// <summary>
    /// Single regression-harness scenario: the resolver context, the target
    /// recording whose pose is sampled, the UT range to sweep, and a
    /// human-readable name for diagnostics.
    /// </summary>
    internal sealed class HarnessScenario
    {
        public string Name { get; }
        public RelativeAnchorResolverContext Context { get; }
        public Recording Target { get; }
        public double StartUT { get; }
        public double EndUT { get; }

        public HarnessScenario(
            string name,
            RelativeAnchorResolverContext context,
            Recording target,
            double startUT,
            double endUT)
        {
            Name = name;
            Context = context;
            Target = target;
            StartUT = startUT;
            EndUT = endUT;
        }
    }
}
