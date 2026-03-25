using Xunit;

namespace Parsek.Tests
{
    public class GhostChainTests
    {
        [Fact]
        public void ChainLink_ToString_IncludesAllFields()
        {
            var link = new ChainLink
            {
                recordingId = "rec-001",
                treeId = "tree-A",
                branchPointId = "bp-1",
                ut = 12345.6,
                interactionType = "MERGE"
            };

            var result = link.ToString();

            Assert.Contains("rec=rec-001", result);
            Assert.Contains("tree=tree-A", result);
            Assert.Contains("type=MERGE", result);
            Assert.Contains("ut=12345.6", result);
        }

        [Fact]
        public void GhostChain_Constructor_InitializesEmptyLinks()
        {
            var chain = new GhostChain();

            Assert.NotNull(chain.Links);
            Assert.Empty(chain.Links);
        }

        [Fact]
        public void GhostChain_ToString_IncludesAllFields()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 42,
                SpawnUT = 17030.5,
                TipRecordingId = "rec-tip",
                TipTreeId = "tree-tip",
                IsTerminated = true
            };
            chain.Links.Add(new ChainLink { recordingId = "r1" });
            chain.Links.Add(new ChainLink { recordingId = "r2" });

            var result = chain.ToString();

            Assert.Contains("vessel=42", result);
            Assert.Contains("links=2", result);
            Assert.Contains("tip=rec-tip", result);
            Assert.Contains("spawnUT=17030.5", result);
            Assert.Contains("terminated=True", result);
        }

        [Fact]
        public void GhostChain_IsTerminated_DefaultsFalse()
        {
            var chain = new GhostChain();

            Assert.False(chain.IsTerminated);
        }

        [Fact]
        public void GhostChain_LinksCanBeAdded_AndRetrieved()
        {
            var chain = new GhostChain();

            var link1 = new ChainLink
            {
                recordingId = "rec-A",
                treeId = "tree-1",
                branchPointId = "bp-10",
                ut = 100.0,
                interactionType = "SPLIT"
            };
            var link2 = new ChainLink
            {
                recordingId = "rec-B",
                treeId = "tree-2",
                branchPointId = null,
                ut = 200.0,
                interactionType = "BACKGROUND_EVENT"
            };

            chain.Links.Add(link1);
            chain.Links.Add(link2);

            Assert.Equal(2, chain.Links.Count);
            Assert.Equal("rec-A", chain.Links[0].recordingId);
            Assert.Equal("SPLIT", chain.Links[0].interactionType);
            Assert.Equal("rec-B", chain.Links[1].recordingId);
            Assert.Equal("BACKGROUND_EVENT", chain.Links[1].interactionType);
            Assert.Null(chain.Links[1].branchPointId);
        }

    }
}
