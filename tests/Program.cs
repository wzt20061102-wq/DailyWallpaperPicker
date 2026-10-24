// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace DailyWallpaperPicker
{
    internal static class TestProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 5 && args[0] == "download" && args[1] == "--request" && args[3] == "--response")
            {
                var request = Json.Read<System.Collections.Generic.Dictionary<string, object>>(args[2]);
                string pidFile = Convert.ToString(request["fixturePidFile"]);
                File.WriteAllText(pidFile, System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
                Json.Write(Convert.ToString(request["progressFile"]), new WorkerProgress { id = "960001", downloaded = 1, total = 10, status = "fixture" });
                Thread.Sleep(60000);
                return 0;
            }
            // Real child process used solely to prove cancellation terminates an owned worker.
            if (args.Length == 2 && args[0] == "--fixture-wait")
            {
                File.WriteAllText(args[1], System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
                Thread.Sleep(60000);
                return 0;
            }
            if (args.Length != 1) { Console.Error.WriteLine("Usage: DailyWallpaperPicker.Tests.exe <new output directory>"); return 2; }
            string output = Path.GetFullPath(args[0]);
            try
            {
                ProcessBoundary.Begin(output);
                return SelfTests.Run(output);
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "test-runner-error.txt"), ex.ToString());
                return 2;
            }
            finally { Processes.BeforeStart = null; }
        }
    }

    internal static class ProcessBoundary
    {
        private static string outputRoot;
        private static int fixtureStarts;
        private static int unexpectedStarts;
        internal static int FixtureStarts { get { return Volatile.Read(ref fixtureStarts); } }
        internal static int UnexpectedStarts { get { return Volatile.Read(ref unexpectedStarts); } }
        internal static bool Enabled { get { return Processes.BeforeStart != null; } }

        internal static void Begin(string output)
        {
            outputRoot = output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Processes.BeforeStart = BeforeStart;
        }

        private static void BeforeStart(string executable, string arguments)
        {
            string ownTestRunner = Assembly.GetExecutingAssembly().Location;
            if (String.Equals(Path.GetFullPath(executable), ownTestRunner, StringComparison.OrdinalIgnoreCase)
                && (arguments.StartsWith("--fixture-wait \"" + outputRoot, StringComparison.OrdinalIgnoreCase)
                    || arguments.StartsWith("download --request \"" + outputRoot, StringComparison.OrdinalIgnoreCase)))
            {
                Interlocked.Increment(ref fixtureStarts);
                return;
            }
            Interlocked.Increment(ref unexpectedStarts);
            throw new InvalidOperationException("Offline tests blocked an unexpected process start: " + Path.GetFileName(executable));
        }
    }
}
