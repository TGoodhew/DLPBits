using Ivi.Visa;
using NationalInstruments.Visa;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DLPBits
{
    internal class Program
    {
        // General next steps
        // TODO: Consistent AnsiConsole prompts and messages
        // TODO: Minimize repeated errors and warnings

        // Named constants to replace magic numbers
        private const int DefaultGpibAddress = 18;
        private const int GpibTimeoutMilliseconds = 2000;
        private const int UserMessageDelayMilliseconds = 1000;
        private const int ErrorMessageDelayMilliseconds = 2000;
        private const int GpibAddressMin = 1;
        private const int GpibAddressMax = 30;
        private const byte DlpStartByte1 = 0x10;
        private const byte DlpStartByte2 = 0x80;
        private const byte DlpEndByte1 = 0x3b;
        private const byte DlpEndByte2 = 0xff;

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
            int gpibIntAddress = DefaultGpibAddress; // This is the default address for the SA
            SemaphoreSlim srqWait = new SemaphoreSlim(0, 1);
            bool bROMRead = false;
            List<byte[]> extractedParts = null;
            NationalInstruments.Visa.ResourceManager resManager = null;
            GpibSession gpibSession = null;

            string pathToFile = @"SRAM_85620A.bin"; // From the KO4BB image here - https://www.ko4bb.com/getsimple/index.php?id=manuals&dir=HP_Agilent_Keysight/HP_85620A

            try
            {
                DisplayTitle(gpibIntAddress, bROMRead, extractedParts);

                // Ask for test choice
                var TestChoice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select the test to run?")
                        .PageSize(10)
                        .AddChoices(new[] { "Set GPIB Address", "Read SRAM Image File", "Clear Mass Memory", "Create DLPs", "Exit" })
                        );

                while (TestChoice != "Exit")
                {
                    try
                    {
                        switch (TestChoice)
                        {
                            case "Set GPIB Address":
                                gpibIntAddress = SetGPIBAddress(gpibIntAddress);
                                break;
                            case "Read SRAM Image File":
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
                                ClearMassMemory(gpibIntAddress, srqWait, ref resManager, ref gpibSession);
                                break;
                            case "Create DLPs":
                                CreateDLPs(extractedParts, ref gpibIntAddress, ref gpibSession, ref resManager, ref srqWait);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Unexpected error in operation: {ex.Message}[/]");
                        Debug.WriteLine($"Unexpected error in Main loop: {ex}");
                        Thread.Sleep(ErrorMessageDelayMilliseconds);
                    }

                    // Clear the screen & Display title
                    DisplayTitle(gpibIntAddress, bROMRead, extractedParts);

                    // Ask for test choice
                    TestChoice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select the test to run?")
                        .PageSize(10)
                        .AddChoices(new[] { "Set GPIB Address", "Read SRAM Image File", "Clear Mass Memory", "Create DLPs", "Exit" })
                        );
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Critical error in application: {ex.Message}[/]");
                Debug.WriteLine($"Critical error in Main: {ex}");
                Thread.Sleep(ErrorMessageDelayMilliseconds);
            }
            finally
            {
                // Dispose of resources to prevent resource leaks
                gpibSession?.Dispose();
                resManager?.Dispose();
                srqWait?.Dispose();
            }
        }

        private static bool ConnectToDevice(int gpibIntAddress, ref SemaphoreSlim srqWait, ref ResourceManager resManager, ref GpibSession gpibSession)
        {
            if (resManager != null && gpibSession != null)
            {
                AnsiConsole.MarkupLine("[yellow]Warning: Already connected. Disconnecting...[/]");
                try
                {
                    gpibSession?.Dispose();
                    resManager?.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error disposing resources during reconnect: {ex}");
                    AnsiConsole.MarkupLine("[yellow]Warning: Error during disconnect, forcing cleanup...[/]");
                }
                finally
                {
                    resManager = null;
                    gpibSession = null;
                }
                Thread.Sleep(500);
            }
            else if (resManager != null || gpibSession != null)
            {
                // Handle inconsistent state
                Debug.WriteLine("Warning: Inconsistent resource state detected");
                try
                {
                    gpibSession?.Dispose();
                    resManager?.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error disposing resources in inconsistent state: {ex}");
                }
                finally
                {
                    resManager = null;
                    gpibSession = null;
                }
            }

            try
            {
                // Setup the GPIB connection via the ResourceManager
                resManager = new NationalInstruments.Visa.ResourceManager();

                // Create a GPIB session for the specified address
                gpibSession = (GpibSession)resManager.Open(string.Format("GPIB0::{0}::INSTR", gpibIntAddress));
                gpibSession.TimeoutMilliseconds = GpibTimeoutMilliseconds; // Set the timeout to be 2s
                gpibSession.TerminationCharacterEnabled = true;
                gpibSession.Clear(); // Clear the session

                var sessionCopy = gpibSession;
                var srqWaitCopy = srqWait;
                gpibSession.ServiceRequest += (sender, e) => SRQHandler(sender, e, sessionCopy, srqWaitCopy);

                var idn = QueryString("ID?", gpibSession);

                if (string.IsNullOrWhiteSpace(idn))
                {
                    AnsiConsole.MarkupLine("[Red]Device failed to connect. Check GPIB address and device state.[/]");
                    Thread.Sleep(UserMessageDelayMilliseconds); // Pause for a moment to let the user see the message
                    resManager = null;
                    gpibSession = null;
                    return false;
                }
                else
                {
                    // Successfully connected
                    AnsiConsole.MarkupLine("[green]Device connected: [/]" + idn);
                    Thread.Sleep(UserMessageDelayMilliseconds); // Pause for a moment to let the user see the message
                    return true;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[Red]Device failed to connect. GPIB Error: {ex.Message}[/]");
                Thread.Sleep(UserMessageDelayMilliseconds); // Pause for a moment to let the user see the message
                resManager = null;
                gpibSession = null;
                return false;
            }
        }

        private static void CreateDLPs(List<byte[]> extractedParts, ref int gpibIntAddress, ref GpibSession gpibSession, ref ResourceManager resManager, ref SemaphoreSlim srqWait)
        {
            try
            {
                //check for null or empty
                if (extractedParts == null || extractedParts.Count == 0)
                {
                    AnsiConsole.MarkupLine($"[Red]No parts available to create DLPs. Please read the ROM first.[/]");
                    Debug.WriteLine("CreateDLPs: No parts available");
                    Thread.Sleep(UserMessageDelayMilliseconds);
                    return;
                }

                if (!ConnectToDevice(gpibIntAddress, ref srqWait, ref resManager, ref gpibSession))
                {
                    AnsiConsole.MarkupLine("[red]Error: Device not connected. Check GPIB address and device state.[/]");
                    Debug.WriteLine("CreateDLPs: Failed to connect to device");
                    Thread.Sleep(UserMessageDelayMilliseconds); // Pause for a moment to let the user see the message
                    return;
                }

                // Use a local variable to avoid using ref parameter in lambda
                var localGpibSession = gpibSession;

                AnsiConsole.Progress()
                    .Start(ctx =>
                    {
                        Debug.WriteLine($"Starting to write DLPs");

                        int partCount = 0;

                        // Define tasks
                        var task1 = ctx.AddTask("[green]Sending DLP Programs[/]", maxValue: extractedParts.Count);

                        while (!ctx.IsFinished && partCount < extractedParts.Count)
                        {
                            try
                            {
                                Debug.WriteLine($"Part {partCount + 1}: {extractedParts[partCount].Length} bytes");

                                // Convert the byte array to a string using UTF-8 encoding
                                string partString = Encoding.UTF8.GetString(extractedParts[partCount]);
                                Debug.WriteLine("FUNCDEF " + partString + ";");

                                localGpibSession.FormattedIO.WriteLine("FUNCDEF " + partString + ";");

                                var errorResponse = QueryString("ERR?", localGpibSession);
                                if (!int.TryParse(errorResponse, NumberStyles.Integer, CultureInfo.InvariantCulture, out var errorResult))
                                {
                                    AnsiConsole.MarkupLine($"[red]Failed to parse error response: '{errorResponse}'[/]");
                                    Debug.WriteLine($"Failed to parse error response: '{errorResponse}'");
                                    break;
                                }

                                if (errorResult != 0)
                                {
                                    AnsiConsole.MarkupLine($"[red]Error writing DLP part {partCount + 1}: Error Code {errorResult}[/]");
                                    Debug.WriteLine($"Error writing DLP part {partCount + 1}: Error Code {errorResult}");
                                    break;
                                }

                                partCount++;

                                Debug.WriteLine($"Completed {partCount} of {extractedParts.Count} parts.");

                                task1.Increment(1);
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[red]Error writing DLP part {partCount + 1}: {ex.Message}[/]");
                                Debug.WriteLine($"Error writing DLP part {partCount + 1}: {ex}");
                                break;
                            }
                        }
                    });
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error in CreateDLPs: {ex.Message}[/]");
                Debug.WriteLine($"Error in CreateDLPs: {ex}");
                Thread.Sleep(UserMessageDelayMilliseconds);
            }
        }

        private static void ClearMassMemory(int gpibIntAddress, SemaphoreSlim srqWait, ref ResourceManager resManager, ref GpibSession gpibSession)
        {
            try
            {
                var confirm = AnsiConsole.Confirm("Are you sure you want to clear mass memory? This action cannot be undone.", false);
                if (!confirm)
                {
                    AnsiConsole.MarkupLine("[yellow]Mass memory clear cancelled.[/]");
                    Debug.WriteLine("ClearMassMemory: Operation cancelled by user");
                    Thread.Sleep(UserMessageDelayMilliseconds);
                    return;
                }

                ConnectToDevice(gpibIntAddress, ref srqWait, ref resManager, ref gpibSession);

                if (resManager != null && gpibSession != null)
                {
                    SendCommand("DISPOSE ALL", gpibSession);
                    AnsiConsole.MarkupLine("[green]Mass memory cleared.[/]");
                    Debug.WriteLine("ClearMassMemory: Mass memory successfully cleared");
                    Thread.Sleep(UserMessageDelayMilliseconds);
                    return;
                }

                AnsiConsole.MarkupLine("[Red]Error: Device not connected. Check GPIB address and device state.[/]");
                Debug.WriteLine("ClearMassMemory: Device not connected");
                Thread.Sleep(UserMessageDelayMilliseconds); // Pause for a moment to let the user see the message
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error clearing mass memory: {ex.Message}[/]");
                Debug.WriteLine($"Error in ClearMassMemory: {ex}");
                Thread.Sleep(UserMessageDelayMilliseconds);
            }
        }

        private static List<byte[]> ReadROM(string pathToFile, ref bool bROMRead)
        {
            byte[] fileBytes = null;

            try
            {
                fileBytes = File.ReadAllBytes(pathToFile);
                Debug.WriteLine($"Read {fileBytes.Length} bytes from file: {pathToFile}");

                // Translate the Mass Memory Module RAM dump image
                // Again, thanks to Kirril for providing the translation algorithm.
                var translatedBytes = new List<byte>();
                for (int p = 0; p < fileBytes.Length; p++)
                {
                    int translatedAddress = AddrXlat(p);
                    
                    if (translatedAddress < 0 || translatedAddress >= fileBytes.Length)
                    {
                        AnsiConsole.MarkupLine($"[red]Address translation error at position {p}[/]");
                        AnsiConsole.MarkupLine($"[red]Translated to: {translatedAddress} (valid range: 0-{fileBytes.Length - 1})[/]");
                        Debug.WriteLine($"Address translation error: {p} -> {translatedAddress} (array length: {fileBytes.Length})");
                        bROMRead = false;
                        return null;
                    }
                    
                    int d = fileBytes[translatedAddress];
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
                byte[] startSequence = new byte[] { DlpStartByte1, DlpStartByte2 }; // 0x10, 0x80 seem to be the start bytes for a DLP
                byte[] endSequence = new byte[] { DlpEndByte1, DlpEndByte2 };   // 0x3b, 0xff seem to be the end bytes for a DLP

                List<byte[]> extractedParts = ExtractPartsBetweenSequences(translatedBytes.ToArray(), startSequence, endSequence);

                Debug.WriteLine($"Found {extractedParts.Count} part(s) between the specified byte sequences.");

                bROMRead = true;

                AnsiConsole.MarkupLine("[green]ROM image read.[/]");
                Thread.Sleep(UserMessageDelayMilliseconds); // Pause for a moment to let the user see the message

                return extractedParts;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error reading file: {ex.Message}[/]");
                Debug.WriteLine($"Error reading ROM file: {ex}");
            }

            AnsiConsole.MarkupLine("[red]ROM image failed to read.[/]");
            Thread.Sleep(UserMessageDelayMilliseconds); // Pause for a moment to let the user see the message

            bROMRead = false;
            return null;
        }

        private static int SetGPIBAddress(int gpibIntAddress)
        {
            try
            {
                // Prompt for the SA GPIB Address
                gpibIntAddress = AnsiConsole.Prompt(
                    new TextPrompt<int>("Enter spectrum analyzer GPIB address (Default is 18)?")
                    .DefaultValue(DefaultGpibAddress)
                    .Validate(n =>
                    {
                        bool isValidAddress = n >= GpibAddressMin && n <= GpibAddressMax;
                        return isValidAddress
                            ? ValidationResult.Success()
                            : ValidationResult.Error($"Address must be between {GpibAddressMin} and {GpibAddressMax}");
                    })
                    );

                AnsiConsole.MarkupLine("[green]GPIB Address updated.[/]");
                Debug.WriteLine($"SetGPIBAddress: GPIB address set to {gpibIntAddress}");
                Thread.Sleep(UserMessageDelayMilliseconds); // Pause for a moment to let the user see the message

                return gpibIntAddress;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error setting GPIB address: {ex.Message}[/]");
                Debug.WriteLine($"Error in SetGPIBAddress: {ex}");
                Thread.Sleep(UserMessageDelayMilliseconds);
                return gpibIntAddress; // Return the original address if error occurs
            }
        }

        // Extracts all byte segments between startSequence and endSequence (exclusive)
        static List<byte[]> ExtractPartsBetweenSequences(byte[] data, byte[] startSequence, byte[] endSequence)
        {
            var parts = new List<byte[]>();
            
            try
            {
                if (data == null || data.Length == 0)
                {
                    Debug.WriteLine("ExtractPartsBetweenSequences: Input data is null or empty");
                    return parts;
                }

                if (startSequence == null || endSequence == null)
                {
                    Debug.WriteLine("ExtractPartsBetweenSequences: Start or end sequence is null");
                    return parts;
                }

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
                        Debug.WriteLine($"ExtractPartsBetweenSequences: Extracted part of {length} bytes");
                    }
                    index = end + endSequence.Length;
                }

                Debug.WriteLine($"ExtractPartsBetweenSequences: Total parts extracted: {parts.Count}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in ExtractPartsBetweenSequences: {ex}");
            }

            return parts;
        }

        // Finds the index of the first occurrence of the sequence in data starting from startIndex
        static int FindSequence(byte[] data, byte[] sequence, int startIndex)
        {
            try
            {
                if (data == null || sequence == null)
                {
                    Debug.WriteLine("FindSequence: Data or sequence is null");
                    return -1;
                }

                if (sequence.Length == 0)
                {
                    Debug.WriteLine("FindSequence: Sequence is empty");
                    return -1;
                }

                if (startIndex < 0 || startIndex > data.Length)
                {
                    Debug.WriteLine($"FindSequence: Invalid start index {startIndex} for data length {data.Length}");
                    return -1;
                }

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
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in FindSequence: {ex}");
            }
            
            return -1;
        }

        private static void DisplayTitle(int gpibIntAddress, bool bROMRead, List<byte[]> extractedParts)
        {
            try
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
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in DisplayTitle: {ex}");
                // Don't show error to user for display issues, just log it
            }
        }

        static private void SendCommand(string command, GpibSession gpibSession)
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

        static private string ReadResponse(GpibSession gpibSession)
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
        static private string QueryString(string command, GpibSession gpibSession)
        {
            SendCommand(command, gpibSession);
            var response = ReadResponse(gpibSession);
            if (string.IsNullOrWhiteSpace(response))
            {
                AnsiConsole.MarkupLine("[yellow]Warning: No response from instrument.[/]");
            }
            return response;
        }

        public static void SRQHandler(object sender, Ivi.Visa.VisaEventArgs e, GpibSession gpibSession, SemaphoreSlim srqWait)
        {
            try
            {
                var gbs = (GpibSession)sender;
                StatusByteFlags sb = gbs.ReadStatusByte();

                Debug.WriteLine($"SRQHandler - Status Byte: {sb}");

                gpibSession.DiscardEvents(EventType.ServiceRequest);

                SendCommand("*CLS", gpibSession);

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