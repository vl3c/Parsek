using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [CollectionDefinition("Sequential", DisableParallelization = true)]
    public sealed class SequentialCollectionDefinition
    {
    }

    public class SequentialCollectionDefinitionTests
    {
        [Fact]
        public void SequentialCollection_DisablesCrossCollectionParallelization()
        {
            var attr = typeof(SequentialCollectionDefinition)
                .GetCustomAttributes(typeof(CollectionDefinitionAttribute), inherit: false)
                .Cast<CollectionDefinitionAttribute>()
                .SingleOrDefault();

            Assert.NotNull(attr);
            Assert.True(attr.DisableParallelization);
        }
    }
}
