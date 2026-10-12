using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Patchwork.Tools
{
    internal static class GenerateIcons
    {
        static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 1)
            {
                Console.Error.WriteLine("Usage: GenerateIcons.exe [output.ico]");
                return 2;
            }

            string target = args.Length == 0
                ? Path.Combine(Environment.CurrentDirectory, "source", "Patchwork.ico")
                : Path.GetFullPath(args[0]);
            string directory = Path.GetDirectoryName(target);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            List<byte[]> frames = new List<byte[]>();
            foreach (int size in Sizes) frames.Add(Encode(size));
            WriteIcon(target, frames);
            Console.WriteLine("Generated " + target);
            return 0;
        }

        static byte[] Encode(int size)
        {
            BitmapSource bitmap = AppIcons.Render(
                Color.FromRgb(0x64, 0x74, 0x8B),
                Color.FromRgb(0xF8, 0xFA, 0xFC), size);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (MemoryStream stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        static void WriteIcon(string target, IList<byte[]> frames)
        {
            const int directorySize = 6;
            const int entrySize = 16;
            int dataOffset = directorySize + entrySize * frames.Count;
            using (FileStream stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)frames.Count);
                int offset = dataOffset;
                for (int i = 0; i < frames.Count; i++)
                {
                    int size = Sizes[i];
                    byte[] frame = frames[i];
                    writer.Write((byte)(size == 256 ? 0 : size));
                    writer.Write((byte)(size == 256 ? 0 : size));
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write((uint)frame.Length);
                    writer.Write((uint)offset);
                    offset = checked(offset + frame.Length);
                }
                foreach (byte[] frame in frames) writer.Write(frame);
            }
        }
    }
}
