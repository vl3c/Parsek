using System;
using System.Collections;
using System.Globalization;
using System.Reflection;

namespace Parsek
{
    /// <summary>
    /// Reads a LOADED RealChute <c>RealChuteModule</c> by reflection, with no reference to the
    /// RealChute assembly, into the same node shape the module persists (one
    /// <c>PARACHUTE</c> child per canopy), so a loaded and an unloaded RealChute part go
    /// through one pure read (<see cref="ParachuteProtoModuleRead.TryReadCanopy"/>,
    /// <see cref="ParachuteProtoModuleRead.TryReadRealChuteCanopyArea"/>).
    /// RealChute (github.com/StupidChris/RealChute, <c>RealChuteModule.cs</c> /
    /// <c>Parachute.cs</c>): the module's public field <c>parachutes</c> is a
    /// <c>List&lt;Parachute&gt;</c>; each <c>Parachute</c> keeps its state in the public string
    /// <c>depState</c> (the <c>DeploymentState</c> setter writes it on every change, and
    /// <c>Parachute.Save</c> persists it) and its public floats <c>deploymentAlt</c>,
    /// <c>preDeployedDiameter</c> and <c>deployedDiameter</c>. Only fields are read: no
    /// RealChute method runs. A stock install has no <c>RealChuteModule</c>, so nothing here
    /// runs there. Takes <c>object</c>, so it is headless-testable over a stand-in type.
    /// </summary>
    internal static class RealChuteLiveRead
    {
        internal const string CanopyListFieldName = "parachutes";
        internal const string CanopyStateFieldName = "depState";
        internal const string CanopyDeployAltFieldName = "deploymentAlt";
        internal const string CanopyPreDeployedDiameterFieldName = "preDeployedDiameter";
        internal const string CanopyDeployedDiameterFieldName = "deployedDiameter";

        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// Pure: is <paramref name="type"/> RealChute's module type (by name, itself or a base
        /// type)? Matched by name because Parsek does not reference RealChute.
        /// </summary>
        internal static bool IsRealChuteModuleType(Type type)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                if (string.Equals(t.Name, ParachuteProtoModuleRead.RealChuteModuleName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Reads a live RealChute module into a <c>MODULE</c> node with one <c>PARACHUTE</c>
        /// child per canopy carrying <c>depState</c>, <c>deploymentAlt</c>,
        /// <c>preDeployedDiameter</c> and <c>deployedDiameter</c> (a field that cannot be read
        /// is left out, which the pure reads treat as unread). False, with
        /// <paramref name="failure"/> naming why, when the module or its canopy list cannot be
        /// read (a RealChute version with another shape, or a module not yet started); a
        /// canopy whose state field is missing is skipped and counted in
        /// <paramref name="unreadCanopies"/>.
        /// </summary>
        internal static bool TryBuildModuleNode(
            object module, out ConfigNode node, out int unreadCanopies, out string failure)
        {
            node = null;
            unreadCanopies = 0;
            failure = null;
            if (module == null)
            {
                failure = "module-null";
                return false;
            }
            FieldInfo listField = module.GetType().GetField(CanopyListFieldName, InstanceFields);
            if (listField == null)
            {
                failure = "canopy-list-field-missing";
                return false;
            }
            IEnumerable canopies;
            try
            {
                canopies = listField.GetValue(module) as IEnumerable;
            }
            catch (Exception)
            {
                failure = "canopy-list-unreadable";
                return false;
            }
            if (canopies == null)
            {
                failure = "canopy-list-null";
                return false;
            }

            node = new ConfigNode("MODULE");
            node.AddValue("name", ParachuteProtoModuleRead.RealChuteModuleName);
            foreach (object canopy in canopies)
            {
                if (canopy == null || !TryReadString(canopy, CanopyStateFieldName, out string depState))
                {
                    unreadCanopies++;
                    continue;
                }
                ConfigNode c = node.AddNode(ParachuteProtoModuleRead.RealChuteCanopyNodeName);
                c.AddValue(CanopyStateFieldName, depState);
                AddNumber(c, canopy, CanopyDeployAltFieldName);
                AddNumber(c, canopy, CanopyPreDeployedDiameterFieldName);
                AddNumber(c, canopy, CanopyDeployedDiameterFieldName);
            }
            return true;
        }

        private static bool TryReadString(object owner, string fieldName, out string value)
        {
            value = null;
            try
            {
                FieldInfo f = owner.GetType().GetField(fieldName, InstanceFields);
                if (f == null || f.FieldType != typeof(string))
                    return false;
                value = (string)f.GetValue(owner);
                return value != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void AddNumber(ConfigNode target, object owner, string fieldName)
        {
            try
            {
                FieldInfo f = owner.GetType().GetField(fieldName, InstanceFields);
                if (f == null)
                    return;
                object raw = f.GetValue(owner);
                double v;
                if (raw is float fl)
                    v = fl;
                else if (raw is double d)
                    v = d;
                else if (raw is int i)
                    v = i;
                else
                    return;
                if (double.IsNaN(v) || double.IsInfinity(v))
                    return;
                target.AddValue(fieldName, v.ToString("R", CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                // Unreadable: left out, which the pure reads treat as unread.
            }
        }
    }
}
