using NationalInstruments.Visa;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Net.WebRequestMethods;

namespace DLPBits
{
    internal class Program
    {
        public static GpibSession gpibSession;
        public static NationalInstruments.Visa.ResourceManager resManager;
        public static int gpibIntAddress = 18;

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
            // Setup the GPIB connection via the ResourceManager
            resManager = new NationalInstruments.Visa.ResourceManager();

            // Create a GPIB session for the specified address
            gpibSession = (GpibSession)resManager.Open(string.Format("GPIB0::{0}::INSTR", gpibIntAddress));
            gpibSession.TimeoutMilliseconds = 2000; // Set the timeout to be 2s
            gpibSession.TerminationCharacterEnabled = true;
            gpibSession.Clear(); // Clear the session

            string pathToFile = @"SRAM_85620A.bin"; // From the KO4BB image here - https://www.ko4bb.com/getsimple/index.php?id=manuals&dir=HP_Agilent_Keysight/HP_85620A
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
                byte[] startSequence = new byte[] { 0x10, 0x80 }; // Example start bytes
                byte[] endSequence = new byte[] { 0x3b, 0xff };   // Example end bytes

                List<byte[]> extractedParts = ExtractPartsBetweenSequences(translatedBytes.ToArray(), startSequence, endSequence);

                Console.WriteLine($"Found {extractedParts.Count} part(s) between the specified byte sequences.");
                for (int i = 0; i < extractedParts.Count; i++)
                {
                    Console.WriteLine($"Part {i + 1}: {extractedParts[i].Length} bytes");

                    // Convert the byte array to a string using UTF-8 encoding
                    string partString = Encoding.UTF8.GetString(extractedParts[i]);
                    Console.WriteLine("FUNCDEF "+partString + ";");
                    gpibSession.FormattedIO.WriteLine("FUNCDEF " + partString + ";");
                    gpibSession.FormattedIO.WriteLine("ERR?");
                    Console.WriteLine("Error: " + gpibSession.FormattedIO.ReadString());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading file: {ex.Message}");
            }

            // Go to local and exit remote mode
            gpibSession.SendRemoteLocalCommand(Ivi.Visa.GpibInstrumentRemoteLocalMode.GoToLocalDeassertRen); // Switch to local mode
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
    }
}