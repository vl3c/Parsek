using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ParsekFlightDebrisPersistenceTests
    {
        [Fact]
        public void DiscardActiveTreeForSuppressedSceneExit_RestoresDebrisPersistenceOverride()
        {
            const int savedDebrisValue = 3;
            int? restoredDebrisValue = null;
            var flight = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));

            try
            {
                ParsekFlight.SetMaxPersistentDebrisOverrideForTesting = value => restoredDebrisValue = value;
                SetPrivateField(flight, "savedMaxPersistentDebris", savedDebrisValue);
                SetPrivateField(flight, "debrisOverrideActive", true);
                SetPrivateField(flight, "activeTree", BuildMinimalActiveTree());

                InvokeDiscardActiveTreeForSuppressedSceneExit(flight);

                Assert.Equal(savedDebrisValue, restoredDebrisValue);
                Assert.False((bool)GetPrivateField(flight, "debrisOverrideActive"));
                Assert.Null(GetPrivateField(flight, "activeTree"));
            }
            finally
            {
                ParsekFlight.ResetDebrisPersistenceOverridesForTesting();
            }
        }

        private static RecordingTree BuildMinimalActiveTree()
        {
            const string recordingId = "debris-restore-root";
            var tree = new RecordingTree
            {
                Id = "debris-restore-tree",
                TreeName = "Debris Restore Test",
                RootRecordingId = recordingId,
                ActiveRecordingId = recordingId
            };
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = recordingId,
                TreeId = tree.Id,
                VesselName = "Debris Restore Test"
            });
            return tree;
        }

        private static void InvokeDiscardActiveTreeForSuppressedSceneExit(ParsekFlight flight)
        {
            MethodInfo method = typeof(ParsekFlight).GetMethod(
                "DiscardActiveTreeForSuppressedSceneExit",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(
                flight,
                new object[]
                {
                    GameScenes.FLIGHT,
                    123.0,
                    "unit-test debris restore",
                    false
                });
        }

        private static object GetPrivateField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return field.GetValue(target);
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(target, value);
        }
    }
}
