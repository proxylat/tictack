using System.Reflection;
using BenchmarkDotNet.Running;

namespace TicTack.Benchmarks;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
}
