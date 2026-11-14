internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var instance = ReadInstanceArgument(args);
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(instance));
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Galathil", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string? ReadInstanceArgument(string[] args)
    {
        if (args.Length == 0) return null;
        if (!args[0].Equals("--ui", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Galathil is a windowed tool. Launch it from MO2 or run it without command-line options.");

        for (var i = 1; i + 1 < args.Length; i++)
        {
            if (args[i].Equals("--mo2-instance", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
