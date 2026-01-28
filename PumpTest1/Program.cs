namespace PumpTest1
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // [수정] DpiUnaware로 설정하여 윈도우가 알아서 확대하도록 함 (레이아웃 고정)
            Application.SetHighDpiMode(HighDpiMode.DpiUnaware);

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }
    }
}