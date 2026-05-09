using System.Globalization;
using System.Threading;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class DiagnosticFormattersTests
    {
        [Fact]
        public void VectorAndQuaternionFormatters_UseInvariantCulture()
        {
            CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
            CultureInfo originalUiCulture = Thread.CurrentThread.CurrentUICulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
                Thread.CurrentThread.CurrentUICulture = new CultureInfo("fr-FR");

                Assert.Equal(
                    "(1.250,-2.500,3.750)",
                    DiagnosticFormatters.FormatVector3(new Vector3(1.25f, -2.5f, 3.75f)));
                Assert.Equal(
                    "(1.250,-2.500,3.750)",
                    DiagnosticFormatters.FormatVector3d(new Vector3d(1.25, -2.5, 3.75)));
                Assert.Equal(
                    "(0.100,0.200,0.300,0.400)",
                    DiagnosticFormatters.FormatQuaternion(new Quaternion(0.1f, 0.2f, 0.3f, 0.4f)));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
                Thread.CurrentThread.CurrentUICulture = originalUiCulture;
            }
        }
    }
}
