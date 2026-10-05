// File: Program.cs
// Entry point. Argument handling and the render loop live elsewhere; this file only
// decides which command was asked for and reports failure in one place.

using System;

namespace LiveSpiceGen
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                return Cli.Run(args, Console.Out, Console.Error);
            }
            catch (Exception ex)
            {
                // One handler for everything, so a failure always ends the same way: a
                // message on stderr and a non-zero exit code. A generator that dies with a
                // stack trace after four hours of rendering tells you nothing about which
                // render failed; the manifest does, and this points at it.
                Console.Error.WriteLine("error: " + ex.Message);
                Console.Error.WriteLine();
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }
    }
}