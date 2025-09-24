using Ivi.Visa;
using NationalInstruments.Visa;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DLPBits
{
    internal class Program
    {
        public static GpibSession gpibSession;
        public static NationalInstruments.Visa.ResourceManager resManager;
        public static int gpibIntAddress = 18;
        public static SemaphoreSlim srqWait = new SemaphoreSlim(0, 1); // use a semaphore to wait for the SRQ events

        public static bool bROMRead = false;
        public static List<byte[]> extractedParts = null;

        // This function translates the address based on the specific algorithm provided.
        // Code provided by https://github.com/KIrill-ka (EEVBlog user https://www.eevblog.com/forum/profile/?u=127220)
        static int AddrXlat(int a) =>
            ((a << 10) & 1024) |
            ((a << 10) & 2048) |
            ((a << 7) & 512) |
            ((a << 10) & 8192) |
            ((a << 10) & 16384) |
            ((a << 2) & 128) |
            ((a >> 1) & 32) |
            ((a >> 4) & 8) |
            ((a >> 4) & 16) |
            ((a >> 3) & 64) |
            ((a >> 2) & 256) |
            ((a << 1) & 4096) |
            ((a >> 11) & 2) |
            ((a >> 11) & 4) |
            ((a >> 14) & 1) |
            (a & 0x18000);

        // This is the main entry point for the application.
        static void Main(string[] args)
        {

            // TODO: Actually implement the connection so that it cares about the GPIB address

            // Setup the GPIB connection via the ResourceManager
            resManager = new NationalInstruments.Visa.ResourceManager();

            // Create a GPIB session for the specified address
            gpibSession = (GpibSession)resManager.Open(string.Format("GPIB0::{0}::INSTR", gpibIntAddress));
            gpibSession.TimeoutMilliseconds = 2000; // Set the timeout to be 2s
            gpibSession.TerminationCharacterEnabled = true;
            gpibSession.Clear(); // Clear the session

            gpibSession.ServiceRequest += SRQHandler;

            // TODO: Add error handling if the device is not found or does not respond
            // TODO: Add code to get location of ROM file

            string pathToFile = @"SRAM_85620A.bin"; // From the KO4BB image here - https://www.ko4bb.com/getsimple/index.php?id=manuals&dir=HP_Agilent_Keysight/HP_85620A

            DisplayeTitle();

            // Ask for test choice
            var TestChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select the test to run?")
                    .PageSize(10)
                    .AddChoices(new[] { "Set GPIB Address", "Read ROM", "Clear Mass Memory", "Create DLPs", "Exit" })
                    );

            while (TestChoice != "Exit")
            {
                switch (TestChoice)
                {
                    case "Set GPIB Address":
                        SetGPIBAddress();
                        break;
                    case "Read ROM":
                        // Get the path to the ROM file
                        var romFilename = AnsiConsole.Prompt<string>(
                            new TextPrompt<string>("Enter path to ROM file:")
                            .DefaultValue(pathToFile)
                            .Validate(filePath => File.Exists(filePath) ? ValidationResult.Success() : ValidationResult.Error("File does not exist"))
                        );
                        // Read the ROM file and extract parts
                        extractedParts = ReadROM(romFilename, ref bROMRead);
                        // update status for part number
                        break;
                    case "Clear Mass Memory":
                        ClearMassMemory();
                        break;
                    case "Create DLPs":
                        CreateDLPs(extractedParts);
                        break;
                }

                // Clear the screen & Display title
                DisplayeTitle();

                // Ask for test choice
                TestChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select the test to run?")
                    .PageSize(10)
                    .AddChoices(new[] { "Set GPIB Address", "Read ROM", "Clear Mass Memory", "Create DLPs", "Exit" })
                    );
            }

            // Go to local and exit remote mode
            gpibSession.SendRemoteLocalCommand(Ivi.Visa.GpibInstrumentRemoteLocalMode.GoToLocalDeassertRen); // Switch to local mode
        }

        private static void CreateDLPs(List<byte[]> extractedParts)
        {
            //check for null or empty
            if (extractedParts == null || extractedParts.Count == 0)
            {
                Console.WriteLine("No parts available to create DLPs. Please read the ROM first.");
                return;
            }

            AnsiConsole.Progress()
                .Start(ctx =>
                {
                    int partCount = 0;

                    // Define tasks
                    var task1 = ctx.AddTask("[green]Sending DLP Programs[/]", maxValue: 278);

                    while (!ctx.IsFinished)
                    {
                        // TODO: Fix up the debug writing here to be more useful

                        //Console.WriteLine($"Part {partCount + 1}: {extractedParts[partCount].Length} bytes");

                        // Convert the byte array to a string using UTF-8 encoding
                        string partString = Encoding.UTF8.GetString(extractedParts[partCount]);
                        Debug.WriteLine("FUNCDEF " + partString + ";");
                        gpibSession.FormattedIO.WriteLine("FUNCDEF " + partString + ";");

                        // TODO: Check for errors here

                        //gpibSession.FormattedIO.WriteLine("ERR?");
                        //Console.WriteLine("Error: " + gpibSession.FormattedIO.ReadString());

                        partCount++;

                        Debug.WriteLine($"Completed {partCount} of {extractedParts.Count} parts.");

                        task1.Increment(1);
                    }
                });
        }

        private static void ClearMassMemory()
        {
            // Add confirmation prompt
            var confirm = AnsiConsole.Confirm("Are you sure you want to clear mass memory? This action cannot be undone.", false);
            if (!confirm)
            {
                AnsiConsole.MarkupLine("[yellow]Mass memory clear cancelled.[/]");
                Thread.Sleep(1000); // Pause for a moment to let the user see the message
                return;
            }

            SendCommand("DISPOSE ALL");
            AnsiConsole.MarkupLine("[green]Mass memory cleared.[/]");

            Thread.Sleep(1000); // Pause for a moment to let the user see the message
        }

        private static List<byte[]> ReadROM(string pathToFile)
        {
            byte[] fileBytes;

            try
            {
                fileBytes = File.ReadAllBytes(pathToFile);
                Console.WriteLine($"Read {fileBytes.Length} bytes from file.");

                // Translate the Mass Memory Module RAM dump image
                // Again, thanks to Kirril for providing the translation algorithm.
                var translatedBytes = new List<byte>();
                for (int p = 0; p < fileBytes.Length; p++)
                {
                    int d = fileBytes[AddrXlat(p)];
                    byte newByte = (byte)(
                        (d >> 7) |
                        ((d << 1) & 2) |
                        ((d << 1) & 4) |
                        ((d << 1) & 8) |
                        ((d >> 2) & 16) |
                        (d & 32) |
                        ((d << 2) & 64) |
                        ((d << 4) & 128)
                    );
                    translatedBytes.Add(newByte);
                }

                // Define the start and end byte sequences
                byte[] startSequence = new byte[] { 0x10, 0x80 }; // 0x10, 0x80 seem to be the start bytes for a DLP
                byte[] endSequence = new byte[] { 0x3b, 0xff };   // 0x3b, 0xff seem to be the end bytes for a DLP

                List<byte[]> extractedParts = ExtractPartsBetweenSequences(translatedBytes.ToArray(), startSequence, endSequence);

                Console.WriteLine($"Found {extractedParts.Count} part(s) between the specified byte sequences.");

                bROMRead = true;

                AnsiConsole.MarkupLine("[green]ROM image read.[/]");
                Thread.Sleep(1000); // Pause for a moment to let the user see the message

                return extractedParts;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading file: {ex.Message}");
            }

            AnsiConsole.MarkupLine("[red]ROM image failed to read.[/]");
            Thread.Sleep(1000); // Pause for a moment to let the user see the message

            bROMRead = false;
            return null;
        }

        private static void SetGPIBAddress()
        {
            // Prompt for the SA GPIB Address
            gpibIntAddress = AnsiConsole.Prompt(
                new TextPrompt<int>("Enter spectrum analyzer GPIB address (Default is 18)?")
                .DefaultValue(18)
                .Validate(n => n >= 1 && n <= 30 ? ValidationResult.Success() : ValidationResult.Error("Address must be between 1 and 30"))
                );

            AnsiConsole.MarkupLine("[green]GPIB Address updated.[/]");
            Thread.Sleep(1000); // Pause for a moment to let the user see the message
        }

        // Extracts all byte segments between startSequence and endSequence (exclusive)
        static List<byte[]> ExtractPartsBetweenSequences(byte[] data, byte[] startSequence, byte[] endSequence)
        {
            var parts = new List<byte[]>();
            int index = 0;

            while (index < data.Length)
            {
                int start = FindSequence(data, startSequence, index);
                if (start == -1) break;
                start += startSequence.Length;

                int end = FindSequence(data, endSequence, start);
                if (end == -1) break;

                int length = end - start;
                if (length > 0)
                {
                    byte[] part = new byte[length];
                    Array.Copy(data, start, part, 0, length);
                    parts.Add(part);
                }
                index = end + endSequence.Length;
            }

            return parts;
        }

        // Finds the index of the first occurrence of the sequence in data starting from startIndex
        static int FindSequence(byte[] data, byte[] sequence, int startIndex)
        {
            for (int i = startIndex; i <= data.Length - sequence.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < sequence.Length; j++)
                {
                    if (data[i + j] != sequence[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private static void DisplayeTitle()
        {
            // Clear screen and display header
            AnsiConsole.Clear();
            AnsiConsole.Write(
                new FigletText("DLPBits")
                    .LeftJustified()
                    .Color(Color.Green));
            AnsiConsole.WriteLine("--------------------------------------------------");
            AnsiConsole.WriteLine("");
            AnsiConsole.WriteLine("DLPBits - DLP Creator for the HP 85671A and 85672A utilities");
            AnsiConsole.WriteLine("");
            AnsiConsole.WriteLine("GPIB Address: " + gpibIntAddress);
            AnsiConsole.WriteLine("");
            string partCountString = (extractedParts != null && extractedParts.Count > 0) ? ", Parts: " + extractedParts.Count.ToString() : "";
            AnsiConsole.WriteLine("ROM Read: " + bROMRead.ToString() + partCountString);
            AnsiConsole.WriteLine("");
        }

        static private void SendCommand(string command)
        {
            try
            {
                gpibSession.FormattedIO.WriteLine(command);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]GPIB Send Error: {ex.Message}[/]");
                Debug.WriteLine($"GPIB Send Error: {ex}");
            }
        }

        static private string ReadResponse()
        {
            try
            {
                return gpibSession.FormattedIO.ReadLine();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]GPIB Read Error: {ex.Message}[/]");
                Debug.WriteLine($"GPIB Read Error: {ex}");
                return string.Empty;
            }
        }
        static private string QueryString(string command)
        {
            SendCommand(command);
            var response = ReadResponse();
            if (string.IsNullOrWhiteSpace(response))
            {
                AnsiConsole.MarkupLine("[yellow]Warning: No response from instrument.[/]");
            }
            return response;
        }

        public static void SRQHandler(object sender, Ivi.Visa.VisaEventArgs e)
        {
            try
            {
                var gbs = (GpibSession)sender;
                StatusByteFlags sb = gbs.ReadStatusByte();

                Debug.WriteLine($"SRQHandler - Status Byte: {sb}");

                gpibSession.DiscardEvents(EventType.ServiceRequest);

                SendCommand("*CLS");

                srqWait.Release();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]SRQ Handler Error: {ex.Message}[/]");
                Debug.WriteLine($"SRQ Handler Error: {ex}");
            }
        }
    }
}