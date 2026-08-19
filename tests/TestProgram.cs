using System;

namespace EasyFM350.Tests
{
    internal static class TestProgram
    {
        private static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            return Tests.Run();
        }
    }
}
