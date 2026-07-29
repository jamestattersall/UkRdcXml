namespace ConsoleUtilities
{
    public class Progress
    {
        const char _block = '■';
        const string _twirl = "-\\|/";

        private int _nSteps;

        private int step = 0;

        private string? _backSpaces;

        public Progress(int nSteps = 10)
        {
            NSteps = nSteps;
            Console.WriteLine();
        }


        public int NSteps
        {
            get => _nSteps;
            set { _nSteps = value; _backSpaces = BackSpaces(); }
        }

        private string BackSpaces()
        {
            //backspaces to begining of line
            return new string((char)0x0008, NSteps + 10);
        }

        public void WriteProgressBar(int currentValue, int maxValue)
        {
            WriteProgressBar((float)currentValue / (float)maxValue);
        }
        public void WriteProgressBar(double currentValue, double maxValue)
        {
            WriteProgressBar((float)currentValue / (float)maxValue);
        }
        public void WriteProgressBar(float currentValue, float maxValue)
        {
            WriteProgressBar(currentValue / maxValue);
        }

        public void WriteProgressBar(float fraction)
        {
            if ((int)(fraction * 1000) > step)
            {
                step = (int)(fraction * 1000);
                Console.Write(_backSpaces);
                Console.Write("[");
                var p = fraction * NSteps;
                for (var i = 0; i <= NSteps; ++i)
                {
                    if (i > p)
                        Console.Write(' ');
                    else
                        Console.Write(_block);
                }
                Console.Write("] " + string.Format("{0,6:###.0%}", fraction));
            }
        }


        public static void WriteProgress(int progress, bool update = false)
        {
            if (update)
                Console.Write("\b");
            Console.Write(_twirl[progress % _twirl.Length]);
        }
    }
}

