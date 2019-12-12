using System;

namespace AliCloudDynamicDNS.Utility
{
    public class ConsoleHelper
    {
        public const string Normal = nameof(Normal);
        public const string Error = nameof(Error);

        public static void WriteDateTimeLine(string msgType, string msg)
        {
            WriteMsgType(msgType);
            Console.WriteLine($"{DateTime.Now:yyyy 年 MM 月 dd 日 HH:mm:ss} - {msg}");
        }

        public static void WriteMessage(string msg) => WriteDateTimeLine(Normal, msg);

        public static void WriteError(string errMsg) => WriteDateTimeLine(errMsg, errMsg);

        private static void WriteMsgType(string msgType)
        {
            switch (msgType)
            {
                case Normal:
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.Write("[正常] ");
                    return;
                case Error:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("[错误] ");
                    Console.ForegroundColor = ConsoleColor.Gray;
                    return;
                default:
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.Write("[正常] ");
                    return;
            }
        }
    }
}