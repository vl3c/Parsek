using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// A child node of the race scenario node that a later task owns (design 6.1: the children
    /// with the lifetime of the save, such as RACE_FACTS). The race scenario hands a handler the
    /// first child named <see cref="ChildNodeName"/> on load and asks it to write that child on
    /// save; every child without a handler is kept and written back verbatim. A handler is added
    /// with its own <c>LoadReconcilePolicy</c> row.
    /// </summary>
    internal interface IRaceScenarioChild
    {
        /// <summary>The child node's name inside the race scenario node.</summary>
        string ChildNodeName { get; }

        /// <summary>Reads the loaded child. Not called when the save has no such child.</summary>
        void Load(ConfigNode child);

        /// <summary>Writes the child into <paramref name="child"/>, an empty node of <see cref="ChildNodeName"/>.</summary>
        void Save(ConfigNode child);
    }

    /// <summary>
    /// Pure: the race scenario node's load dispatch and its verbatim write-back over the child
    /// handlers. The kept node is the one the save carried; nothing here drops a value or a node
    /// this build does not understand.
    /// </summary>
    internal static class RaceScenarioChildren
    {
        /// <summary>The values <c>ScenarioModule.Save</c> writes itself before the module's OnSave.</summary>
        internal static bool IsStockModuleValue(string name)
        {
            return name == "name" || name == "scene";
        }

        /// <summary>
        /// Hands each handler the first child of its name in <paramref name="kept"/>. Returns how
        /// many handlers received a child.
        /// </summary>
        internal static int DispatchLoad(ConfigNode kept, IList<IRaceScenarioChild> handlers)
        {
            if (kept == null || handlers == null)
                return 0;
            int handled = 0;
            for (int i = 0; i < handlers.Count; i++)
            {
                IRaceScenarioChild handler = handlers[i];
                if (handler == null)
                    continue;
                ConfigNode child = kept.GetNode(handler.ChildNodeName);
                if (child == null)
                    continue;
                handler.Load(child);
                handled++;
            }
            return handled;
        }

        /// <summary>
        /// Writes <paramref name="kept"/> into <paramref name="target"/> in its own order, minus
        /// the stock <c>name</c> / <c>scene</c> values: every value verbatim, every child verbatim
        /// except the first child of a handler's name, which that handler writes in its place.
        /// A handler whose child the save did not carry appends its child when it wrote anything.
        /// With <paramref name="handlers"/> null every child is written verbatim. Returns the
        /// number of entries (values plus children) written; <paramref name="handlerWritten"/>
        /// counts the children the handlers wrote.
        /// </summary>
        internal static int WriteBack(ConfigNode kept, ConfigNode target,
            IList<IRaceScenarioChild> handlers, out int handlerWritten)
        {
            handlerWritten = 0;
            if (target == null)
                return 0;
            int written = 0;
            var placed = new HashSet<string>();
            if (kept != null)
            {
                for (int i = 0; i < kept.values.Count; i++)
                {
                    var v = kept.values[i];
                    if (v == null || IsStockModuleValue(v.name))
                        continue;
                    target.AddValue(v.name, v.value);
                    written++;
                }
                for (int i = 0; i < kept.nodes.Count; i++)
                {
                    ConfigNode child = kept.nodes[i];
                    if (child == null)
                        continue;
                    IRaceScenarioChild handler = placed.Contains(child.name)
                        ? null
                        : FindHandler(handlers, child.name);
                    if (handler != null)
                    {
                        placed.Add(child.name);
                        var fresh = new ConfigNode(child.name);
                        handler.Save(fresh);
                        target.AddNode(fresh);
                        handlerWritten++;
                    }
                    else
                    {
                        target.AddNode(child.CreateCopy());
                    }
                    written++;
                }
            }
            if (handlers != null)
            {
                for (int i = 0; i < handlers.Count; i++)
                {
                    IRaceScenarioChild handler = handlers[i];
                    if (handler == null || placed.Contains(handler.ChildNodeName))
                        continue;
                    placed.Add(handler.ChildNodeName);
                    var fresh = new ConfigNode(handler.ChildNodeName);
                    handler.Save(fresh);
                    if (fresh.values.Count == 0 && fresh.nodes.Count == 0)
                        continue;
                    target.AddNode(fresh);
                    handlerWritten++;
                    written++;
                }
            }
            return written;
        }

        private static IRaceScenarioChild FindHandler(IList<IRaceScenarioChild> handlers, string name)
        {
            if (handlers == null || name == null)
                return null;
            for (int i = 0; i < handlers.Count; i++)
            {
                IRaceScenarioChild handler = handlers[i];
                if (handler != null && handler.ChildNodeName == name)
                    return handler;
            }
            return null;
        }
    }
}
