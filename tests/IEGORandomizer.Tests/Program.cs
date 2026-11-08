using System.Reflection;

namespace IEGORandomizer.Tests
{
    public static class Program
    {
        public static int Main()
        {
            return TestRunner.RunAll(Assembly.GetExecutingAssembly());
        }
    }
}
