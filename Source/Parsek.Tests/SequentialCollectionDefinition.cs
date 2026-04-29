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
                .Single();
            var attrData = typeof(SequentialCollectionDefinition)
                .GetCustomAttributesData()
                .Single(a => a.AttributeType == typeof(CollectionDefinitionAttribute));

            Assert.Equal("Sequential", attrData.ConstructorArguments.Single().Value);
            Assert.True(attr.DisableParallelization);
        }
    }
}
