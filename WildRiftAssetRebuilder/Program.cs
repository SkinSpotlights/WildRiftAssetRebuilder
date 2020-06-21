using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WildRiftAssetRebuilder
{
    class Program
    {
        //Files currently failing with current Iteration.
        //Note: These files contain PNGs so we can CRC Check the chunks.
        /*
            07b5cc52edeecbc3f4fdcc64a0954cdc
            462b12de040707b8f0115dea19eac8c6
            468c03161adfb189481aa772c613bfa1
            514a47ec99668d8495e771e574cb21d3
            7ae5384a01ee76a8e16c4e5dc0d7fd59 - 667kb
            881b6a8f3fa7942a4cd07e7fea4f7c6f
            91b49efe4e4b4c11c37228a0ab864c00 - Twisted Fate
            a610fab65bd17217f61e42e08d5af2b4
            c409d0a61f281ecc57e27238f4a7fc43
            ebbbfbfdf97e0b8337ec000bcf3ffb48
            f502c2de23a016945cce5beaa4747eec
         */

        /*
         * 7ae5384a01ee76a8e16c4e5dc0d7fd59 - Camille, 667kb
         * Fails to decode correctly.
         *
         * File is 100% LZ4 Encoded past PNG IEND Tag.
         * Algorithm safety check fails on 2nd Chunk and subsequent chunks
         * File doesn't seem to follow the "0x20000" Size rule like some other files do
         * Other files uncompressed chunk size will always be 0x20000 til EOF
         *
         * Camille .bytes seems to include multiple files, but algorithm fails too early
         * for that to be the main factor/cause for the issue
         *
         * Chunk 2 has to be either encoded or unencoded with a uncompressed size less than 0x20000
         *
         * The header file which is encrypted normally contains a block list which contains
         * Compressed Size, Uncompressed Size and "compression" flags (LZ4/LZMA/Uncompressed)
         *
         * This algorithm attempts reverse the process from the data using the rule that Uncompressed Data
         * has to be 0x20000 (Only less if we hit EOF), some files don't seem to follow this rule and unsure
         * how it deviates from the pattern.
         * Random Unity Android Games AssetBundles were used in order to figure/make this "rule".
         *
         * Decrypting the Header might be the only option to 100% to read the files successfully.
         */

        static void Main(string[] args)
        {
            byte[] ToRebuild = File.ReadAllBytes(@"E:\WILDRIFT\7ae5384a01ee76a8e16c4e5dc0d7fd59.bytes");
            //Find where data starts, find the 2018.4.16f tag and -0x16 and thats where data starts encoded.
            List<int> offsets =
                SearchBytePattern(new Byte[] { 0x32, 0x30, 0x31, 0x38, 0x2E, 0x34, 0x2E, 0x31, 0x36, 0x66, 0x31 },
                    ToRebuild);
            if (offsets.Count == 0) return;
            int offset = offsets[0] - 0x16;

            //Array of file to test the data
            List<Byte[]> decodedBytesList = new List<byte[]>();
            //List of blocks we are recreating
            List<int> compressedList = new List<int>();
            List<int> decompressedList = new List<int>();
            int decodedSize = 0x20000;
            var compressedStream = new MemoryStream(ToRebuild);
            using (var lz4Stream = new Lz4(compressedStream))
            {
                lz4Stream.input.Position = offset;
                while (offset != ToRebuild.Length)
                {
                    Byte[] decodedBytes = new byte[decodedSize];
                    //Read the bytes til we hit 0x20000
                    //Unity max decompressed size for a chunk is 0x20000
                    int decompressedSize = lz4Stream.Read(decodedBytes, 0, decodedSize);

                    if (lz4Stream.input.Position != ToRebuild.Length)
                    {
                        int overflowCorrection = lz4Stream.inBufEnd - lz4Stream.inBufPos;
                        lz4Stream.input.Position -= overflowCorrection;
                    }
                    //Calculate the compressed size fo the bytes
                    int compressedSize = (int)lz4Stream.input.Position - offset;

                    //Check to see if we successfully decoded the array and theres not actually "more data"
                    //Unsure if a failed decode = rest of file is not encoded.
                    //In most of my checks it seems to be the case
                    bool decodeFailed =  lz4Stream.phase == Lz4.DecodePhase.CopyLiteral && lz4Stream.litLen != 0
                                         //|| lz4Stream.phase == Lz4.DecodePhase.CopyMatch && lz4Stream.matLen != 0
                                         || lz4Stream.phase != Lz4.DecodePhase.CopyLiteral;
                                         //&& lz4Stream.phase != Lz4.DecodePhase.CopyMatch;

                    bool loop = true;
                    //Comment out the while to make it check every chunk
                    while (loop && offset != ToRebuild.Length)
                    {
                        if (decodeFailed)
                        {
                            int size = decodedSize;
                            if (offset + size > ToRebuild.Length)
                                size = ToRebuild.Length - offset;
                            decompressedSize = size;
                            compressedSize = size;
                            lz4Stream.input.Position = offset + size;
                        }

                        if (decodedBytes.Length != decompressedSize)
                            decodedBytes = decodedBytes.Resize(decompressedSize);


                        if (compressedSize == decompressedSize)
                        {
                            //Allocate a new array otherwise we overwrite the others in the list
                            decodedBytes = new byte[decompressedSize];
                            Buffer.BlockCopy(ToRebuild, offset, decodedBytes, 0, decodedBytes.Length);
                        }

                        decodedBytesList.Add(decodedBytes);
                        //Store Compressed/Decompressed Data so we can remake
                        //the proper Unity Header and not Riots encrypted one
                        compressedList.Add(compressedSize);
                        decompressedList.Add(decompressedSize);
                        //Reset the lz4 Stream data, otherwise bad time
                        lz4Stream.Reset();
                        offset += compressedSize;
                        //If the decode failed keep looping til we hit EOF
                        loop = decodeFailed;
                    }
                }
            }

            //Concat all the data
            Byte[] decodedFile = decodedBytesList.SelectMany(byteArr => byteArr).ToArray();
            File.WriteAllBytes(@"E:\WILDRIFT\TestDump123.dat", decodedFile);

            //TODO: Actually get it 100% working with every file
            //TODO: Recreate the header
        }

        static public List<int> SearchBytePattern(byte[] pattern, byte[] bytes)
        {
            List<int> positions = new List<int>();
            int patternLength = pattern.Length;
            int totalLength = bytes.Length;
            byte firstMatchByte = pattern[0];
            for (int i = 0; i < totalLength; i++)
            {
                if (firstMatchByte == bytes[i] && totalLength - i >= patternLength)
                {
                    byte[] match = new byte[patternLength];
                    Array.Copy(bytes, i, match, 0, patternLength);
                    if (match.SequenceEqual<byte>(pattern))
                    {
                        positions.Add(i);
                        i += patternLength - 1;
                    }
                }
            }

            return positions;
        }
    }

    public static class ByteArrayExtensions
    {
        public static byte[] Resize(this byte[] byteArray, int len)
        {
            byte[] tmp = new byte[len];
            Array.Copy(byteArray, tmp, len);

            return tmp;
        }
    }
}
