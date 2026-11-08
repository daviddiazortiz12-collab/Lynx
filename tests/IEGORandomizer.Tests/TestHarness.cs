using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace IEGORandomizer.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class FactAttribute : Attribute
    {
    }

    public sealed class TestFailureException : Exception
    {
        public TestFailureException(string message) : base(message)
        {
        }
    }

    public static class Assert
    {
        public static void True(bool condition, string message = "Expected condition to be true.")
        {
            if (!condition)
                throw new TestFailureException(message);
        }

        public static void False(bool condition, string message = "Expected condition to be false.")
        {
            if (condition)
                throw new TestFailureException(message);
        }

        public static void Equal<T>(T expected, T actual, string message = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new TestFailureException(
                    message ?? $"Assert.Equal failed. Expected: <{expected}>, Actual: <{actual}>.");
            }
        }

        public static void NotEqual<T>(T notExpected, T actual, string message = null)
        {
            if (EqualityComparer<T>.Default.Equals(notExpected, actual))
            {
                throw new TestFailureException(
                    message ?? $"Assert.NotEqual failed. Both values were <{actual}>.");
            }
        }

        public static void NotNull(object value, string message = "Expected non-null reference.")
        {
            if (value == null)
                throw new TestFailureException(message);
        }

        public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message = null)
        {
            var expList = expected.ToList();
            var actList = actual.ToList();
            if (expList.Count != actList.Count)
            {
                throw new TestFailureException(
                    message ?? $"Sequence lengths differ: expected {expList.Count}, actual {actList.Count}.");
            }
            for (int i = 0; i < expList.Count; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(expList[i], actList[i]))
                {
                    throw new TestFailureException(
                        message ?? $"Sequence mismatch at index {i}: expected <{expList[i]}>, actual <{actList[i]}>.");
                }
            }
        }

        public static TException Throws<TException>(Action action, string message = null)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                throw new TestFailureException(
                    message ?? $"Expected exception {typeof(TException).Name}, but caught {ex.GetType().Name}: {ex.Message}");
            }

            throw new TestFailureException(
                message ?? $"Expected exception {typeof(TException).Name}, but no exception was thrown.");
        }
    }

    public static class TestRunner
    {
        public static int RunAll(Assembly assembly)
        {
            var testMethods = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                .Where(m => m.GetCustomAttribute<FactAttribute>() != null)
                .OrderBy(m => m.DeclaringType?.Name)
                .ThenBy(m => m.Name)
                .ToList();

            int passed = 0;
            int failed = 0;
            var totalSw = Stopwatch.StartNew();

            Console.WriteLine($"Running {testMethods.Count} tests in {assembly.GetName().Name}...");
            foreach (MethodInfo method in testMethods)
            {
                string fullName = $"{method.DeclaringType?.Name}.{method.Name}";
                var sw = Stopwatch.StartNew();
                try
                {
                    object instance = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!);
                    method.Invoke(instance, null);
                    sw.Stop();
                    passed++;
                    Console.WriteLine($"  [PASS] {fullName} ({sw.ElapsedMilliseconds} ms)");
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    sw.Stop();
                    failed++;
                    Console.WriteLine($"  [FAIL] {fullName}: {tie.InnerException.Message}");
                    Console.WriteLine($"         {tie.InnerException.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    failed++;
                    Console.WriteLine($"  [FAIL] {fullName}: {ex.Message}");
                }
            }

            totalSw.Stop();
            Console.WriteLine($"Summary: {passed} passed, {failed} failed ({totalSw.ElapsedMilliseconds} ms total).");
            return failed == 0 ? 0 : 1;
        }
    }
}
