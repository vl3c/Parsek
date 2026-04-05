using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Lightweight assertion helpers for in-game tests.
    /// Throws <see cref="InGameTestFailedException"/> on failure.
    /// </summary>
    public static class InGameAssert
    {
        public static void IsTrue(bool condition, string message = null)
        {
            if (!condition)
                throw new InGameTestFailedException(message ?? "Expected true but was false");
        }

        public static void IsFalse(bool condition, string message = null)
        {
            if (condition)
                throw new InGameTestFailedException(message ?? "Expected false but was true");
        }

        public static void IsNull(object value, string message = null)
        {
            // Unity overloads == for destroyed objects, so use the Unity-aware check
            if (value is UnityEngine.Object uObj)
            {
                if (uObj != null)
                    throw new InGameTestFailedException(message ?? $"Expected null but was {value}");
            }
            else if (value != null)
            {
                throw new InGameTestFailedException(message ?? $"Expected null but was {value}");
            }
        }

        public static void IsNotNull(object value, string message = null)
        {
            if (value is UnityEngine.Object uObj)
            {
                if (uObj == null)
                    throw new InGameTestFailedException(message ?? "Expected non-null but was null (or destroyed)");
            }
            else if (value == null)
            {
                throw new InGameTestFailedException(message ?? "Expected non-null but was null");
            }
        }

        public static void AreEqual<T>(T expected, T actual, string message = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InGameTestFailedException(
                    message ?? $"Expected <{expected}> but was <{actual}>");
        }

        public static void AreNotEqual<T>(T expected, T actual, string message = null)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InGameTestFailedException(
                    message ?? $"Expected values to differ but both were <{actual}>");
        }

        public static void IsGreaterThan(double value, double threshold, string message = null)
        {
            if (value <= threshold)
                throw new InGameTestFailedException(
                    message ?? $"Expected {value} > {threshold}");
        }

        public static void IsLessThan(double value, double threshold, string message = null)
        {
            if (value >= threshold)
                throw new InGameTestFailedException(
                    message ?? $"Expected {value} < {threshold}");
        }

        public static void ApproxEqual(float expected, float actual, float tolerance = 0.001f, string message = null)
        {
            if (Mathf.Abs(expected - actual) > tolerance)
                throw new InGameTestFailedException(
                    message ?? $"Expected ~{expected} but was {actual} (tolerance={tolerance})");
        }

        public static void ApproxEqual(double expected, double actual, double tolerance = 0.001, string message = null)
        {
            if (Math.Abs(expected - actual) > tolerance)
                throw new InGameTestFailedException(
                    message ?? $"Expected ~{expected} but was {actual} (tolerance={tolerance})");
        }

        public static void ApproxEqual(Vector3 expected, Vector3 actual, float tolerance = 0.01f, string message = null)
        {
            float dist = Vector3.Distance(expected, actual);
            if (dist > tolerance)
                throw new InGameTestFailedException(
                    message ?? $"Expected ~{expected} but was {actual} (distance={dist:F4}, tolerance={tolerance})");
        }

        public static void Contains(string haystack, string needle, string message = null)
        {
            if (haystack == null || !haystack.Contains(needle))
                throw new InGameTestFailedException(
                    message ?? $"Expected string to contain \"{needle}\" but was \"{haystack ?? "(null)"}\"");
        }

        public static void IsNotEmpty<T>(ICollection<T> collection, string message = null)
        {
            if (collection == null || collection.Count == 0)
                throw new InGameTestFailedException(message ?? "Expected non-empty collection");
        }

        public static void Fail(string message)
        {
            throw new InGameTestFailedException(message);
        }

        /// <summary>
        /// Marks the test as Skipped (not Passed, not Failed) when a precondition
        /// is not met. Use instead of silent early-return so the test runner can
        /// distinguish "tested and passed" from "could not test".
        /// </summary>
        public static void Skip(string reason)
        {
            throw new InGameTestSkippedException(reason);
        }
    }

    public class InGameTestFailedException : Exception
    {
        public InGameTestFailedException(string message) : base(message) { }
    }

    public class InGameTestSkippedException : Exception
    {
        public InGameTestSkippedException(string message) : base(message) { }
    }
}
