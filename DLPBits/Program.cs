﻿using Ivi.Visa;
using NationalInstruments.Visa;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            int gpibIntAddress = 18; // This is the default address for the SA
            SemaphoreSlim srqWait = new SemaphoreSlim(0, 1);
            bool bROMRead = false;
            List<byte[]> extractedParts = null;
            NationalInstruments.Visa.ResourceManager resManager = null;
            GpibSession gpibSession = null;

            string pathToFile = @"SRAM_85620A.bin"; // From the KO4BB image here - https://www.ko4bb.com/getsimple/index.php?id=manuals&dir=HP_Agilent_Keysight/HP_85620A

            DisplayeTitle(gpibIntAddress, bROMRead, extractedParts);

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
                        gpibIntAddress = SetGPIBAddress(gpibIntAddress);
                        break;
                    case "Read ROM":
                        // Get the path to the ROM file
                        AnsiConsole.Prompt<string>(
                            new TextPrompt<string>("Enter path to ROM file:")
                            .DefaultValue(pathToFile)
                            .Validate(filePath => File.Exists(filePath) ? ValidationResult.Success() : ValidationResult.Error("File does not exist"))
                        );
                        // Read the ROM file and extract parts
                        extractedParts = ReadROM(pathToFile, ref bROMRead);
                        // update status for part number
                        break;
                    case "Clear Mass Memory":
                        ClearMassMemory(gpibIntAddress, srqWait, ref resManager, ref gpibSession);
                        break;
                    case "Create DLPs":
                        CreateDLPs(extractedParts, ref gpibIntAddress, ref gpibSession, ref resManager, ref srqWait);
                        break;
                }

                // Clear the screen & Display title
                DisplayeTitle(gpibIntAddress, bROMRead, extractedParts);

                // Ask for test choice
                TestChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select the test to run?")
                    .PageSize(10)
                    .AddChoices(new[] { "Set GPIB Address", "Read ROM", "Clear Mass Memory", "Create DLPs", "Exit" })
                    );
            }
        }

        private static bool ConnectToDevice(int gpibIntAddress, ref SemaphoreSlim srqWait, ref ResourceManager resManager, ref GpibSession gpibSession)
        {
            if (resManager != null || gpibSession != null)
            {
                AnsiConsole.MarkupLine("[yellow]Warning: Already connected to a device. Disconnecting and reconnecting.[/]");
                gpibSession?.Dispose();
                resManager?.Dispose();
                resManager = null;
                gpibSession = null;
                Thread.Sleep(1000); // Pause for a moment to let the user see the message
            }

            try
            {
                // Setup the GPIB connection via the ResourceManager
                resManager = new NationalInstruments.Visa.ResourceManager();

                // Create a GPIB session for the specified address
                gpibSession = (GpibSession)resManager.Open(string.Format("GPIB0::{0}::INSTR", gpibIntAddress));
                gpibSession.TimeoutMilliseconds = 2000; // Set the timeout to be 2s
                gpibSession.TerminationCharacterEnabled = true;
                gpibSession.Clear(); // Clear the session

                var sessionCopy = gpibSession;
                var srqWaitCopy = srqWait;
                gpibSession.ServiceRequest += (sender, e) => SRQHandler(sender, e, sessionCopy, srqWaitCopy);

                var idn = QueryString("ID?", gpibSession);

                if (string.IsNullOrWhiteSpace(idn))
                {
                    AnsiConsole.MarkupLine("[Red]Device failed to connect. Check GPIB address and device state.[/]");
                    Thread.Sleep(1000); // Pause for a moment to let the user see the message
                    resManager = null;
                    gpibSession = null;
                    return false;
                }
                else
                {
                    // Successfully connected
                    AnsiConsole.MarkupLine("[green]Device connected: [/]" + idn);
                    Thread.Sleep(1000); // Pause for a moment to let the user see the message
                    return true;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[Red]Device failed to connect. GPIB Error: {ex.Message}[/]");
                Thread.Sleep(1000); // Pause for a moment to let the user see the message
                resManager = null;
                gpibSession = null;
                return false;
            }
        }

        private static void CreateDLPs(List<byte[]> extractedParts, ref int gpibIntAddress, ref GpibSession gpibSession, ref ResourceManager resManager, ref SemaphoreSlim srqWait)
        {
            //check for null or empty
            if (extractedParts == null || extractedParts.Count == 0)
            {
                AnsiConsole.MarkupLine($"[Red]No parts available to create DLPs. Please read the ROM first.[/]");
                Thread.Sleep(1000);
                return;
            }

            if (!ConnectToDevice(gpibIntAddress, ref srqWait, ref resManager, ref gpibSession))
            {
                AnsiConsole.MarkupLine("[red]Error: Device not connected. Check GPIB address and device state.[/]");
                Thread.Sleep(1000); // Pause for a moment to let the user see the message
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
                    var task1 = ctx.AddTask("[green]Sending DLP Programs[/]", maxValue: 278);

                    while (!ctx.IsFinished)
                    {
                        Debug.WriteLine($"Part {partCount + 1}: {extractedParts[partCount].Length} bytes");

                        // Convert the byte array to a string using UTF-8 encoding
                        string partString = Encoding.UTF8.GetString(extractedParts[partCount]);
                        Debug.WriteLine("FUNCDEF " + partString + ";");

                        localGpibSession.FormattedIO.WriteLine("FUNCDEF " + partString + ";");

                        var errorResult = Convert.ToInt32(QueryString("ERR?", localGpibSession));

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
                });
        }

        private static void ClearMassMemory(int gpibIntAddress, SemaphoreSlim srqWait, ref ResourceManager resManager, ref GpibSession gpibSession)
        {
            var confirm = AnsiConsole.Confirm("Are you sure you want to clear mass memory? This action cannot be undone.", false);
            if (!confirm)
            {
                AnsiConsole.MarkupLine("[yellow]Mass memory clear cancelled.[/]");
                Thread.Sleep(1000);
                return;
            }

            ConnectToDevice(gpibIntAddress, ref srqWait, ref resManager, ref gpibSession);

            if (resManager != null || gpibSession != null)
            {
                SendCommand("DISPOSE ALL", gpibSession);
                AnsiConsole.MarkupLine("[green]Mass memory cleared.[/]");
                Thread.Sleep(1000);
                return;
            }

            AnsiConsole.MarkupLine("[Red]Error: Device not connected. Check GPIB address and device state.[/]");
            Thread.Sleep(1000); // Pause for a moment to let the user see the message
        }

        private static List<byte[]> ReadROM(string pathToFile, ref bool bROMRead)
        {
            byte[] fileBytes = null;

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

        private static int SetGPIBAddress(int gpibIntAddress)
        {
            // Prompt for the SA GPIB Address
            gpibIntAddress = AnsiConsole.Prompt(
                new TextPrompt<int>("Enter spectrum analyzer GPIB address (Default is 18)?")
                .DefaultValue(18)
                .Validate(n => n >= 1 && n <= 30 ? ValidationResult.Success() : ValidationResult.Error("Address must be between 1 and 30"))
                );

            AnsiConsole.MarkupLine("[green]GPIB Address updated.[/]");
            Thread.Sleep(1000); // Pause for a moment to let the user see the message

            return gpibIntAddress;
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

        private static void DisplayeTitle(int gpibIntAddress, bool bROMRead, List<byte[]> extractedParts)
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