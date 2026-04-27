using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Text;
using System.Windows.Forms.VisualStyles;

namespace Labrune
{
    public class LanguageChunk : Chunk
    {
        public LanguageFileVersion Version = LanguageFileVersion.Unknown;
        public int NumberOfStringRecords;
        public int StringRecordsOffset;
        public int TextOffset;
        public int CharsetOffset;
        public string Category; // Size: 0x10 (new files only)
        public Charset CharacterSet; // Old chunks only, from 0x10 to the StringRecordsOffset
        public List<LanguageStringRecord> Strings;

        public LanguageChunk(Chunk chunk, Charset characterset)
        {
            CharacterSet = characterset;
            Offset = chunk.Offset;
            ID = chunk.ID;
            Size = chunk.Size;
            Data = chunk.Data;

            var br = new BinaryReader(new MemoryStream(Data));
            Read(br);
            br.Close();
        }

        public LanguageChunk(Chunk chunk)
        {
            Offset = chunk.Offset;
            ID = chunk.ID;
            Size = chunk.Size;
            Data = chunk.Data;

            var br = new BinaryReader(new MemoryStream(Data));
            Read(br);
            br.Close();
        }

        public LanguageChunk()
        {

        }

        public void DetectVersion(BinaryReader br)
        {
            // Chunk Size = Hash Table Size + Strings Size + Hash Table Offset
            // MW: (_0x0C * 8) + (_0x04 - _0x14) + _0x10
            // C: (_0x08 * 8) + (_0x04 - _0x10) + _0x0C

            uint _Offset = (uint)br.BaseStream.Position; // Get position to go back later

            // Get values required to detect the chunk type
            uint _0x08 = br.ReadUInt32();
            uint _0x0C = br.ReadUInt32();
            uint _0x10 = br.ReadUInt32();
            uint _0x14 = br.ReadUInt32();

            // Calculate according to MW
            if ((_0x0C * 8) + (Size - _0x14) + _0x10 == Size) Version = LanguageFileVersion.Old;

            // Calculate according to C
            if ((_0x08 * 8) + (Size - _0x10) + _0x0C == Size) Version = LanguageFileVersion.New;

            // Go back
            br.BaseStream.Position = _Offset;
        }

        public byte[] ReadNullTerminated(BinaryReader br)
        {
            var bldr = new List<Byte>();
            byte nc;
            while ((nc = br.ReadByte()) > 0)
                bldr.Add(nc);

            return bldr.ToArray();
        }

        /// <summary>
        /// Try to decode bytes as UTF-8. Returns null if the bytes are not valid UTF-8.
        /// This helps detect if the original file used UTF-8 encoding.
        /// </summary>
        private string TryDecodeAsUTF8(byte[] bytes)
        {
            try
            {
                // Check if bytes are valid UTF-8
                string decodedText = System.Text.Encoding.UTF8.GetString(bytes);
                
                // Verify the decoded text doesn't contain replacement characters
                // (which would indicate invalid UTF-8)
                if (!decodedText.Contains("\ufffd"))
                {
                    // Additional check: if all bytes are < 128 (ASCII), it's valid UTF-8
                    // If there are bytes >= 128, check if they form valid UTF-8 sequences
                    bool hasHighBytes = false;
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        if (bytes[i] >= 0x80)
                        {
                            hasHighBytes = true;
                            break;
                        }
                    }
                    
                    // If no high bytes, it's plain ASCII (valid UTF-8)
                    // If high bytes exist and decoded without replacement chars, it's valid UTF-8
                    if (!hasHighBytes || decodedText.Length > 0)
                    {
                        return decodedText;
                    }
                }
            }
            catch
            {
                // Invalid UTF-8, return null to fall back to ISO-8859-1
            }
            
            return null;
        }

        public override void Read(BinaryReader br)
        {
            DetectVersion(br); // Check if it's old (MW, U, U2) style or new (C+) style.

            // Get file info according to version
            switch (Version)
            {
                case LanguageFileVersion.Old:
                    CharsetOffset = br.ReadInt32();
                    NumberOfStringRecords = br.ReadInt32();
                    StringRecordsOffset = br.ReadInt32();
                    TextOffset = br.ReadInt32();
                    Category = System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(br.ReadBytes(CharsetOffset - 0x10)).Trim('\0', ' ');
                    CharacterSet = new Charset();
                    CharacterSet.Read(br); //0x1804 fixed size
                    break;

                case LanguageFileVersion.New:
                    NumberOfStringRecords = br.ReadInt32();
                    StringRecordsOffset = br.ReadInt32();
                    TextOffset = br.ReadInt32();
                    Category = System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(ReadNullTerminated(br));
                    break;

                case LanguageFileVersion.Unknown:
                    throw new Exception("Unknown language chunk version");
                    break;
            }

            // Read strings
            Strings = new List<LanguageStringRecord>();

            br.BaseStream.Position = StringRecordsOffset;

            for (int i = 0; i < NumberOfStringRecords; i++) // Traverse all the string records and read them
            {
                br.BaseStream.Position = StringRecordsOffset + i * 8;

                var StrRec = new LanguageStringRecord();

                StrRec.Hash = br.ReadUInt32();

                br.BaseStream.Position = TextOffset + br.ReadInt32();
                byte[] rawBytes = ReadNullTerminated(br);
                
                if (CharacterSet != null)
                {
                    StrRec.Text = CharacterSet.Decode(rawBytes);
                }
                else
                {
                    // Try to detect encoding: UTF-8 first, then ISO-8859-1
                    StrRec.Text = TryDecodeAsUTF8(rawBytes) ?? System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(rawBytes);
                }
                StrRec.IsModified = false;

                Strings.Add(StrRec);
            }

            br.BaseStream.Position = Size;

        }

        public override void Write(BinaryWriter bw)
        {
            var LanguageHashTable = new MemoryStream();
            var LanguageStringTable = new MemoryStream();
            var LanguageHashTableWriter = new BinaryWriter(LanguageHashTable);
            var LanguageStringTableWriter = new BinaryWriter(LanguageStringTable);

            byte[] LangFileCategory;

            // Validate that all characters can be encoded with the current Charset
            if (CharacterSet != null)
            {
                ValidateCharactersInCharset();
            }

            foreach (LanguageStringRecord StrRec in Strings)
            {
                // Write hash and offset for the hashes table
                LanguageHashTableWriter.Write(StrRec.Hash);
                LanguageHashTableWriter.Write((int)LanguageStringTableWriter.BaseStream.Position);

                // Write string for the strings table
                byte[] encodedBytes;
                if (CharacterSet != null)
                {
                    // Encode using the charset (no fallback - ensures compatibility with game engine)
                    encodedBytes = CharacterSet.Encode(StrRec.Text);
                }
                else
                {
                    // No charset available, try UTF-8 first (for universal language support), then ISO-8859-1
                    try
                    {
                        encodedBytes = System.Text.Encoding.UTF8.GetBytes(StrRec.Text + "\0");
                    }
                    catch
                    {
                        encodedBytes = System.Text.Encoding.GetEncoding("ISO-8859-1").GetBytes(StrRec.Text + "\0");
                    }
                }
                LanguageStringTableWriter.Write(encodedBytes);
            }

            // Fix strings table size to %4
            int PaddingDifference = ((int)LanguageStringTableWriter.BaseStream.Length % 4);
            while (PaddingDifference != 0)
            {
                LanguageStringTableWriter.Write((byte)0);
                PaddingDifference = (PaddingDifference + 1) % 4;
            }

            // Create the chunk data to write
            var LanguageChunkData = new MemoryStream();
            var LanguageChunkDataWriter = new BinaryWriter(LanguageChunkData);

            switch(Version)
            {
                case LanguageFileVersion.Old:
                    LanguageChunkDataWriter.Write(CharsetOffset);
                    LanguageChunkDataWriter.Write(Strings.Count); // Number of string records
                    LanguageChunkDataWriter.Write(CharsetOffset + CharacterSet.Size()); // Hash table (string records) offset
                    LanguageChunkDataWriter.Write(CharsetOffset + CharacterSet.Size() + (int)LanguageHashTableWriter.BaseStream.Length); // String table (text) offset
                    if (Category != "") // Category (usually added by binary). Size = (CharsetOffset - 0x10)
                    {
                        LangFileCategory = Encoding.GetEncoding("ISO-8859-1").GetBytes(Category);
                        Array.Resize(ref LangFileCategory, CharsetOffset - 0x10);
                        LanguageChunkDataWriter.Write(LangFileCategory);
                    }
                    CharacterSet.Write(LanguageChunkDataWriter);
                    LanguageChunkDataWriter.Write(LanguageHashTable.ToArray());
                    LanguageChunkDataWriter.Write(LanguageStringTable.ToArray());
                    break;
                case LanguageFileVersion.New:
                default:
                    LanguageChunkDataWriter.Write(Strings.Count); // Number of string records
                    LanguageChunkDataWriter.Write(StringRecordsOffset); // Hash table (string records) offset
                    LanguageChunkDataWriter.Write(StringRecordsOffset + (int)LanguageHashTableWriter.BaseStream.Length); // String table (text) offset
                    // Category (StringRecordsOffset - 0x0C)
                    LangFileCategory = Encoding.GetEncoding("ISO-8859-1").GetBytes(Category);
                    Array.Resize(ref LangFileCategory, StringRecordsOffset - 0x0C);
                    LanguageChunkDataWriter.Write(LangFileCategory);
                    LanguageChunkDataWriter.Write(LanguageHashTable.ToArray());
                    LanguageChunkDataWriter.Write(LanguageStringTable.ToArray());
                    break;
            }

            // Write the stuff to file
            bw.Write(ID); // Write chunk ID
            bw.Write((int)LanguageChunkData.Length); // Write new chunk size
            bw.Write(LanguageChunkData.ToArray()); // Write new chunk data

            // Get rid of all the streams and writers
            LanguageStringTableWriter.Dispose();
            LanguageStringTableWriter.Close();
            LanguageStringTable.Dispose();
            LanguageStringTable.Close();
            LanguageHashTableWriter.Dispose();
            LanguageHashTableWriter.Close();
            LanguageHashTable.Dispose();
            LanguageHashTable.Close();
            LanguageChunkDataWriter.Dispose();
            LanguageChunkDataWriter.Close();
            LanguageChunkData.Dispose();
            LanguageChunkData.Close();
        }

        private void ValidateCharactersInCharset()
        {
            var missingCharacters = new List<char>();
            var checkedChars = new HashSet<char>();

            foreach (LanguageStringRecord strRec in Strings)
            {
                foreach (char c in strRec.Text)
                {
                    if (!checkedChars.Contains(c))
                    {
                        checkedChars.Add(c);
                        try
                        {
                            // Test if this character can be encoded
                            CharacterSet.Encode(c.ToString());
                        }
                        catch
                        {
                            if (!missingCharacters.Contains(c))
                            {
                                missingCharacters.Add(c);
                            }
                        }
                    }
                }
            }

            if (missingCharacters.Count > 0)
            {
                var missingCharsStr = new System.Text.StringBuilder();
                foreach (char c in missingCharacters)
                {
                    missingCharsStr.Append(c);
                    missingCharsStr.Append(" ");
                }

                throw new Exception(
                    $"Cannot save file: The Charset table is missing {missingCharacters.Count} character(s).\\n\\n" +
                    $"Missing characters: {missingCharsStr}\\n\\n" +
                    $"To fix this issue:\\n" +
                    $"1. Use a file format without embedded Charset (newer game versions)\\n" +
                    $"2. Or expand the Charset table to include these characters\\n" +
                    $"3. Or use only characters supported by the current Charset table"
                );
            }
        }
    }

    public enum LanguageFileVersion
    {
        Old, New, Unknown
    };
}
