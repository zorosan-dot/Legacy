namespace Dragon_Radar
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                ApplicationConfiguration.Initialize();
                Application.Run(new DragonRadarUI());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fatal Startup Error:\n\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}", 
                    "Dragon Radar - Crash on Startup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
