using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Parsek
{
    /// <summary>
    /// In-memory ledger of all game actions, with file persistence.
    /// Single source of truth for recalculation — loaded from a dedicated
    /// sidecar file under saves/&lt;save&gt;/Parsek/GameState/ledger.pgld.
    /// </summary>
    internal static class Ledger
    {
        private const int CurrentLedgerVersion = 1;
        private const string RootNodeName = "LEDGER";
        private const string ActionNodeName = "GAME_ACTION";
        private const string VersionKey = "version";

        private static List<GameAction> actions = new List<GameAction>();

        /// <summary>Read-only view of all actions in the ledger.</summary>
        internal static IReadOnlyList<GameAction> Actions => actions;

        /// <summary>Appends a single action to the in-memory ledger.</summary>
        internal static void AddAction(GameAction action)
        {
            if (action == null)
            {
                ParsekLog.Warn("Ledger", "AddAction called with null action, skipping");
                return;
            }

            actions.Add(action);
            ParsekLog.Verbose("Ledger",
                $"Added action: type={action.Type}, ut={action.UT.ToString("R", CultureInfo.InvariantCulture)}, " +
                $"recordingId={action.RecordingId ?? "(none)"}, total={actions.Count}");
        }

        /// <summary>Batch-appends multiple actions to the in-memory ledger.</summary>
        internal static void AddActions(IEnumerable<GameAction> newActions)
        {
            if (newActions == null)
            {
                ParsekLog.Warn("Ledger", "AddActions called with null collection, skipping");
                return;
            }

            int before = actions.Count;
            foreach (var action in newActions)
            {
                if (action != null)
                    actions.Add(action);
            }
            int added = actions.Count - before;
            ParsekLog.Verbose("Ledger", $"AddActions batch: added={added}, total={actions.Count}");
        }

        /// <summary>Clears all actions from the in-memory ledger.</summary>
        internal static void Clear()
        {
            int count = actions.Count;
            actions.Clear();
            ParsekLog.Verbose("Ledger", $"Cleared ledger: removed={count}");
        }

        // ================================================================
        // File I/O
        // ================================================================

        /// <summary>
        /// Serializes all in-memory actions to a ConfigNode file using safe-write (.tmp + rename).
        /// Returns true on success.
        /// </summary>
        internal static bool SaveToFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                ParsekLog.Warn("Ledger", "SaveToFile called with null/empty path");
                return false;
            }

            try
            {
                var root = new ConfigNode(RootNodeName);
                root.AddValue(VersionKey, CurrentLedgerVersion.ToString(CultureInfo.InvariantCulture));

                for (int i = 0; i < actions.Count; i++)
                {
                    actions[i].SerializeInto(root);
                }

                SafeWriteConfigNode(root, path);

                ParsekLog.Verbose("Ledger",
                    $"Saved ledger to '{path}': version={CurrentLedgerVersion}, actions={actions.Count}");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Ledger", $"Failed to save ledger to '{path}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deserializes all actions from a ConfigNode file, replacing the in-memory list.
        /// Returns true on success. On failure, the in-memory list is cleared.
        /// </summary>
        internal static bool LoadFromFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                ParsekLog.Warn("Ledger", "LoadFromFile called with null/empty path");
                actions.Clear();
                return false;
            }

            if (!File.Exists(path))
            {
                ParsekLog.Verbose("Ledger", $"Ledger file not found at '{path}', starting with empty ledger");
                actions.Clear();
                return true;
            }

            try
            {
                // ConfigNode.Load returns a node containing the file contents directly.
                // The root node name from Save becomes the loaded node's name.
                ConfigNode loaded = ConfigNode.Load(path);
                if (loaded == null)
                {
                    ParsekLog.Warn("Ledger", $"ConfigNode.Load returned null for '{path}', corrupt file?");
                    actions.Clear();
                    return false;
                }

                // Check version
                string versionStr = loaded.GetValue(VersionKey);
                int version = 0;
                if (versionStr != null)
                    int.TryParse(versionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out version);

                if (version < 1)
                {
                    ParsekLog.Warn("Ledger",
                        $"Ledger file '{path}' has unsupported version '{versionStr ?? "(missing)"}', starting with empty ledger");
                    actions.Clear();
                    return false;
                }

                // Deserialize actions
                var newActions = new List<GameAction>();
                ConfigNode[] actionNodes = loaded.GetNodes(ActionNodeName);
                int parseErrors = 0;

                for (int i = 0; i < actionNodes.Length; i++)
                {
                    try
                    {
                        var action = GameAction.DeserializeFrom(actionNodes[i]);
                        newActions.Add(action);
                    }
                    catch (Exception ex)
                    {
                        parseErrors++;
                        ParsekLog.Warn("Ledger",
                            $"Failed to deserialize action at index {i}: {ex.Message}");
                    }
                }

                actions = newActions;
                ParsekLog.Verbose("Ledger",
                    $"Loaded ledger from '{path}': version={version}, actions={actions.Count}, parseErrors={parseErrors}");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Ledger", $"Failed to load ledger from '{path}': {ex.Message}");
                actions.Clear();
                return false;
            }
        }

        // ================================================================
        // Reconciliation
        // ================================================================

        /// <summary>
        /// Prunes the in-memory ledger against the current save state:
        /// - Earning actions whose recordingId is NOT in validRecordingIds are removed.
        /// - Spending actions whose UT is strictly after maxUT are removed.
        /// Earnings and spendings are classified by <see cref="RecalculationEngine.IsEarningType"/>
        /// and <see cref="RecalculationEngine.IsSpendingType"/>.
        /// FundsInitial actions are always kept.
        /// </summary>
        internal static void Reconcile(HashSet<string> validRecordingIds, double maxUT)
        {
            if (validRecordingIds == null)
            {
                ParsekLog.Warn("Ledger", "Reconcile called with null validRecordingIds, skipping");
                return;
            }

            int before = actions.Count;
            int prunedEarnings = 0;
            int prunedSpendings = 0;
            int prunedOther = 0;
            int kept = 0;

            var surviving = new List<GameAction>(actions.Count);

            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];

                // Seed actions are always kept — they're immutable initial balances, not earnings or spendings
                if (action.Type == GameActionType.FundsInitial ||
                    action.Type == GameActionType.ScienceInitial ||
                    action.Type == GameActionType.ReputationInitial)
                {
                    surviving.Add(action);
                    kept++;
                    continue;
                }

                // Earning actions: classified by type, validated by recordingId.
                // Null recordingId is valid for KSC spending actions (milestones achieved
                // outside any recording, e.g. FirstCrewToSurvive at recovery).
                if (RecalculationEngine.IsEarningType(action.Type))
                {
                    if (action.RecordingId == null || validRecordingIds.Contains(action.RecordingId))
                    {
                        surviving.Add(action);
                        kept++;
                    }
                    else
                    {
                        prunedEarnings++;
                        ParsekLog.Verbose("Ledger",
                            $"Pruned earning: type={action.Type}, " +
                            $"recordingId='{action.RecordingId}' not in validRecordingIds, " +
                            $"UT={action.UT.ToString("R", CultureInfo.InvariantCulture)}");
                    }
                    continue;
                }

                // Spending actions: classified by type, pruned by UT
                if (RecalculationEngine.IsSpendingType(action.Type))
                {
                    if (action.UT > maxUT)
                    {
                        prunedSpendings++;
                        ParsekLog.Verbose("Ledger",
                            $"Pruned spending: type={action.Type}, " +
                            $"UT={action.UT.ToString("R", CultureInfo.InvariantCulture)} > " +
                            $"maxUT={maxUT.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"recordingId='{action.RecordingId ?? "(null)"}'");
                    }
                    else
                    {
                        surviving.Add(action);
                        kept++;
                    }
                    continue;
                }

                // Other action types (e.g. ContractAccept, KerbalAssignment, etc.)
                // that are neither earning nor spending: keep if recordingId is valid
                // or if they have no recordingId and UT is within range
                if (action.RecordingId != null)
                {
                    if (validRecordingIds.Contains(action.RecordingId))
                    {
                        surviving.Add(action);
                        kept++;
                    }
                    else
                    {
                        prunedOther++;
                        ParsekLog.Verbose("Ledger",
                            $"Pruned other: type={action.Type}, " +
                            $"recordingId='{action.RecordingId}' not in validRecordingIds, " +
                            $"UT={action.UT.ToString("R", CultureInfo.InvariantCulture)}");
                    }
                }
                else if (action.UT <= maxUT)
                {
                    surviving.Add(action);
                    kept++;
                }
                else
                {
                    prunedOther++;
                    ParsekLog.Verbose("Ledger",
                        $"Pruned other: type={action.Type}, " +
                        $"recordingId=(null), " +
                        $"UT={action.UT.ToString("R", CultureInfo.InvariantCulture)} > " +
                        $"maxUT={maxUT.ToString("R", CultureInfo.InvariantCulture)}");
                }
            }

            actions = surviving;

            ParsekLog.Info("Ledger",
                $"Reconcile complete: before={before}, kept={kept}, " +
                $"prunedEarnings={prunedEarnings}, prunedSpendings={prunedSpendings}, " +
                $"prunedOther={prunedOther}, " +
                $"maxUT={maxUT.ToString("R", CultureInfo.InvariantCulture)}, " +
                $"validRecordingIds={validRecordingIds.Count}");
        }

        // ================================================================
        // Path resolution
        // ================================================================

        /// <summary>
        /// Resolves the full filesystem path to the ledger file for the current save.
        /// Returns null if the KSP context is not available.
        /// </summary>
        internal static string GetLedgerPath()
        {
            string dir = RecordingPaths.EnsureGameStateDirectory();
            if (dir == null)
            {
                ParsekLog.Warn("Ledger", "GetLedgerPath: could not resolve game state directory");
                return null;
            }

            return RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildLedgerRelativePath());
        }

        // ================================================================
        // Seeding
        // ================================================================

        /// <summary>
        /// Creates a FundsInitial action if none exists in the current ledger.
        /// Called once when Parsek first initializes on a career save.
        /// The seed is immutable — subsequent calls are no-ops.
        /// </summary>
        internal static void SeedInitialFunds(double initialFunds)
        {
            // Check if a FundsInitial action already exists
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i].Type == GameActionType.FundsInitial)
                {
                    // Update a stale 0-value seed: during a previous load, the seed may
                    // have been created with 0 before KSP populated Funding.Instance.
                    if (actions[i].InitialFunds == 0f && initialFunds != 0.0)
                    {
                        actions[i].InitialFunds = (float)initialFunds;
                        ParsekLog.Info("Ledger",
                            $"SeedInitialFunds: updated stale 0-value seed to {initialFunds.ToString("R", CultureInfo.InvariantCulture)}");
                        return;
                    }

                    ParsekLog.Verbose("Ledger",
                        $"SeedInitialFunds: FundsInitial already exists (amount={actions[i].InitialFunds.ToString("R", CultureInfo.InvariantCulture)}), " +
                        $"ignoring new seed amount={initialFunds.ToString("R", CultureInfo.InvariantCulture)}");
                    return;
                }
            }

            var seed = new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = (float)initialFunds
            };

            actions.Add(seed);
            ParsekLog.Info("Ledger",
                $"Seeded initial funds: amount={initialFunds.ToString("R", CultureInfo.InvariantCulture)}, total={actions.Count}");
        }

        /// <summary>
        /// Creates a ScienceInitial action if none exists in the current ledger.
        /// Called once when Parsek first initializes on a save with existing science.
        /// The seed is immutable — subsequent calls are no-ops.
        /// </summary>
        internal static void SeedInitialScience(float initialScience)
        {
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i].Type == GameActionType.ScienceInitial)
                {
                    if (actions[i].InitialScience == 0f && initialScience != 0f)
                    {
                        actions[i].InitialScience = initialScience;
                        ParsekLog.Info("Ledger",
                            $"SeedInitialScience: updated stale 0-value seed to {initialScience.ToString("R", CultureInfo.InvariantCulture)}");
                        return;
                    }

                    ParsekLog.Verbose("Ledger",
                        $"SeedInitialScience: ScienceInitial already exists (amount={actions[i].InitialScience.ToString("R", CultureInfo.InvariantCulture)}), " +
                        $"ignoring new seed amount={initialScience.ToString("R", CultureInfo.InvariantCulture)}");
                    return;
                }
            }

            var seed = new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ScienceInitial,
                InitialScience = initialScience
            };

            actions.Add(seed);
            ParsekLog.Info("Ledger",
                $"Seeded initial science: amount={initialScience.ToString("R", CultureInfo.InvariantCulture)}, total={actions.Count}");
        }

        /// <summary>
        /// Creates a ReputationInitial action if none exists in the current ledger.
        /// Called once when Parsek first initializes on a save with existing reputation.
        /// The seed is immutable — subsequent calls are no-ops.
        /// </summary>
        internal static void SeedInitialReputation(float initialReputation)
        {
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i].Type == GameActionType.ReputationInitial)
                {
                    if (actions[i].InitialReputation == 0f && initialReputation != 0f)
                    {
                        actions[i].InitialReputation = initialReputation;
                        ParsekLog.Info("Ledger",
                            $"SeedInitialReputation: updated stale 0-value seed to {initialReputation.ToString("R", CultureInfo.InvariantCulture)}");
                        return;
                    }

                    ParsekLog.Verbose("Ledger",
                        $"SeedInitialReputation: ReputationInitial already exists (amount={actions[i].InitialReputation.ToString("R", CultureInfo.InvariantCulture)}), " +
                        $"ignoring new seed amount={initialReputation.ToString("R", CultureInfo.InvariantCulture)}");
                    return;
                }
            }

            var seed = new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ReputationInitial,
                InitialReputation = initialReputation
            };

            actions.Add(seed);
            ParsekLog.Info("Ledger",
                $"Seeded initial reputation: amount={initialReputation.ToString("R", CultureInfo.InvariantCulture)}, total={actions.Count}");
        }

        // ================================================================
        // Testing support
        // ================================================================

        /// <summary>Clears all state for test isolation.</summary>
        internal static void ResetForTesting()
        {
            actions = new List<GameAction>();
        }

        // ================================================================
        // Safe-write (matches RecordingStore pattern)
        // ================================================================

        private static void SafeWriteConfigNode(ConfigNode node, string path)
        {
            // Ensure parent directory exists
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = path + ".tmp";
            node.Save(tmpPath);

            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("Ledger", $"Failed to delete existing file '{path}': {ex.Message}");
                    throw;
                }
            }

            try
            {
                File.Move(tmpPath, path);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Ledger", $"Failed to move temp file '{tmpPath}' to '{path}': {ex.Message}");
                throw;
            }
        }
    }
}
