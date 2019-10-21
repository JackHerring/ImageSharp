// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;

using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.Memory;

namespace SixLabors.ImageSharp.Formats.WebP
{
    /// <summary>
    /// Performs the bitmap decoding operation.
    /// </summary>
    internal sealed class WebPDecoderCore
    {
        /// <summary>
        /// Reusable buffer.
        /// </summary>
        private readonly byte[] buffer = new byte[4];

        /// <summary>
        /// The global configuration.
        /// </summary>
        private readonly Configuration configuration;

        /// <summary>
        /// Used for allocating memory during processing operations.
        /// </summary>
        private readonly MemoryAllocator memoryAllocator;

        /// <summary>
        /// The bitmap decoder options.
        /// </summary>
        private readonly IWebPDecoderOptions options;

        /// <summary>
        /// The stream to decode from.
        /// </summary>
        private Stream currentStream;

        /// <summary>
        /// The metadata.
        /// </summary>
        private ImageMetadata metadata;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebPDecoderCore"/> class.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        /// <param name="options">The options.</param>
        public WebPDecoderCore(Configuration configuration, IWebPDecoderOptions options)
        {
            this.configuration = configuration;
            this.memoryAllocator = configuration.MemoryAllocator;
            this.options = options;
        }

        public Image<TPixel> Decode<TPixel>(Stream stream)
            where TPixel : struct, IPixel<TPixel>
        {
            var metadata = new ImageMetadata();
            WebPMetadata webpMetadata = metadata.GetFormatMetadata(WebPFormat.Instance);
            this.currentStream = stream;

            uint chunkSize = this.ReadImageHeader();
            WebPImageInfo imageInfo = this.ReadVp8Info();

            var image = new Image<TPixel>(this.configuration, imageInfo.Width, imageInfo.Height, this.metadata);
            Buffer2D<TPixel> pixels = image.GetRootFramePixelBuffer();
            if (imageInfo.IsLossLess)
            {
                ReadSimpleLossless(pixels, image.Width, image.Height);
            }
            else
            {
                ReadSimpleLossy(pixels, image.Width, image.Height);
            }

            // TODO: there can be optional chunks after the image data, like EXIF, XMP etc.

            return image;
        }

        /// <summary>
        /// Reads the raw image information from the specified stream.
        /// </summary>
        /// <param name="stream">The <see cref="Stream"/> containing image data.</param>
        public IImageInfo Identify(Stream stream)
        {
            var metadata = new ImageMetadata();
            WebPMetadata webpMetadata = metadata.GetFormatMetadata(WebPFormat.Instance);
            this.currentStream = stream;

            this.ReadImageHeader();
            WebPImageInfo imageInfo = this.ReadVp8Info();

            // TODO: not sure yet where to get this info. Assuming 24 bits for now.
            int bitsPerPixel = 24;
            return new ImageInfo(new PixelTypeInfo(bitsPerPixel), imageInfo.Width, imageInfo.Height, this.metadata);
        }

        private uint ReadImageHeader()
        {
            // Skip FourCC header, we already know its a RIFF file at this point.
            this.currentStream.Skip(4);

            // Read Chunk size.
            // The size of the file in bytes starting at offset 8.
            // The file size in the header is the total size of the chunks that follow plus 4 bytes for the ‘WEBP’ FourCC.
            this.currentStream.Read(this.buffer, 0, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(this.buffer);

            // Skip 'WEBP' from the header.
            this.currentStream.Skip(4);

            return chunkSize;
        }

        private WebPImageInfo ReadVp8Info(int vpxWidth = 0, int vpxHeight = 0)
        {
            // Read VP8 chunk header.
            this.currentStream.Read(this.buffer, 0, 4);

            if (this.buffer.AsSpan().SequenceEqual(WebPConstants.Vp8Header))
            {
                return this.ReadVp8Header(vpxWidth, vpxHeight);
            }

            if (this.buffer.AsSpan().SequenceEqual(WebPConstants.Vp8LHeader))
            {
                return this.ReadVp8LHeader(vpxWidth, vpxHeight);
            }

            if (this.buffer.SequenceEqual(WebPConstants.Vp8XHeader))
            {
                return this.ReadVp8XHeader();
            }

            WebPThrowHelper.ThrowImageFormatException("Unrecognized VP8 header");

            return new WebPImageInfo();
        }

        private WebPImageInfo ReadVp8XHeader()
        {
            this.currentStream.Read(this.buffer, 0, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(this.buffer);

            // This byte contains information about the image features used.
            // The first two bit should and the last bit should be 0.
            // TODO: should an exception be thrown if its not the case, or just ignore it?
            byte imageFeatures = (byte)this.currentStream.ReadByte();

            // If bit 3 is set, a ICC Profile Chunk should be present.
            bool isIccPresent = (imageFeatures & (1 << 5)) != 0;

            // If bit 4 is set, any of the frames of the image contain transparency information ("alpha" chunk).
            bool isAlphaPresent = (imageFeatures & (1 << 4)) != 0;

            // If bit 5 is set, a EXIF metadata should be present.
            bool isExifPresent = (imageFeatures & (1 << 3)) != 0;

            // If bit 6 is set, XMP metadata should be present.
            bool isXmpPresent = (imageFeatures & (1 << 2)) != 0;

            // If bit 7 is set, animation should be present.
            bool isAnimationPresent = (imageFeatures & (1 << 7)) != 0;

            // 3 reserved bytes should follow which are supposed to be zero.
            this.currentStream.Read(this.buffer, 0, 3);

            // 3 bytes for the width.
            this.currentStream.Read(this.buffer, 0, 3);
            this.buffer[3] = 0;
            int width = BinaryPrimitives.ReadInt32LittleEndian(this.buffer) + 1;

            // 3 bytes for the height.
            this.currentStream.Read(this.buffer, 0, 3);
            this.buffer[3] = 0;
            int height = BinaryPrimitives.ReadInt32LittleEndian(this.buffer) + 1;

            // TODO: optional chunks ICCP and ANIM can follow here. Ignoring them for now.

            // A VP8 or VP8L chunk will follow here.
            return this.ReadVp8Info(width, height);
        }

        private WebPImageInfo ReadVp8Header(int vpxWidth = 0, int vpxHeight = 0)
        {
            // VP8 data size.
            this.currentStream.Read(this.buffer, 0, 3);
            this.buffer[3] = 0;
            uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(this.buffer);

            // https://tools.ietf.org/html/rfc6386#page-30
            // Frame tag that contains four fields:
            // - A 1-bit frame type (0 for key frames, 1 for interframes).
            // - A 3-bit version number.
            // - A 1-bit show_frame flag.
            // - A 19-bit field containing the size of the first data partition in bytes.
            this.currentStream.Read(this.buffer, 0, 3);
            int tmp = (this.buffer[2] << 16) | (this.buffer[1] << 8) | this.buffer[0];
            int isKeyFrame = tmp & 0x1;
            int version = (tmp >> 1) & 0x7;
            int showFrame = (tmp >> 4) & 0x1;

            // Check for VP8 magic bytes.
            this.currentStream.Read(this.buffer, 0, 4);
            if (!this.buffer.AsSpan(1).SequenceEqual(WebPConstants.Vp8MagicBytes))
            {
                WebPThrowHelper.ThrowImageFormatException("VP8 magic bytes not found");
            }

            this.currentStream.Read(this.buffer, 0, 4);

            // TODO: Get horizontal and vertical scale
            int width = BinaryPrimitives.ReadInt16LittleEndian(this.buffer) & 0x3fff;
            int height = BinaryPrimitives.ReadInt16LittleEndian(this.buffer.AsSpan(2)) & 0x3fff;

            // Use the width and height from the VP8X information, if its provided, because its 3 bytes instead of 14 bits.
            bool isVpxDimensionsPresent = vpxHeight != 0 || vpxWidth != 0;

            return new WebPImageInfo()
                   {
                       Width = isVpxDimensionsPresent ? vpxWidth : width,
                       Height = isVpxDimensionsPresent ? vpxHeight : height,
                       IsLossLess = false,
                       DataSize = dataSize
                   };
        }

        private WebPImageInfo ReadVp8LHeader(int vpxWidth = 0, int vpxHeight = 0)
        {
            // VP8 data size.
            this.currentStream.Read(this.buffer, 0, 4);
            uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(this.buffer);

            // One byte signature, should be 0x2f.
            byte signature = (byte)this.currentStream.ReadByte();
            if (signature != WebPConstants.Vp8LMagicByte)
            {
                WebPThrowHelper.ThrowImageFormatException("Invalid VP8L signature");
            }

            // The first 28 bits of the bitstream specify the width and height of the image.
            var bitReader = new Vp8LBitReader(this.currentStream);
            uint width = bitReader.Read(WebPConstants.Vp8LImageSizeBits) + 1;
            uint height = bitReader.Read(WebPConstants.Vp8LImageSizeBits) + 1;

            // The alpha_is_used flag should be set to 0 when all alpha values are 255 in the picture, and 1 otherwise.
            bool alphaIsUsed = bitReader.ReadBit();

            // The next 3 bytes are the version. The version_number is a 3 bit code that must be set to 0.
            // Any other value should be treated as an error.
            uint version = bitReader.Read(3);
            if (version != 0)
            {
                WebPThrowHelper.ThrowImageFormatException($"Unexpected webp version number: {version}");
            }

            // Next bit indicates, if a transformation is present.
            bool transformPresent = bitReader.ReadBit();
            int numberOfTransformsPresent = 0;
            while (transformPresent)
            {
                var transformType = (WebPTransformType)bitReader.Read(2);
                switch (transformType)
                {
                    case WebPTransformType.SubtractGreen:
                        // There is no data associated with this transform.
                        break;
                    case WebPTransformType.ColorIndexingTransform:
                        // The transform data contains color table size and the entries in the color table.
                        // 8 bit value for color table size.
                        uint colorTableSize = bitReader.Read(8) + 1;
                        // TODO: color table should follow here?
                        break;

                    case WebPTransformType.PredictorTransform:
                    {
                        // The first 3 bits of prediction data define the block width and height in number of bits.
                        // The number of block columns, block_xsize, is used in indexing two-dimensionally.
                        uint sizeBits = bitReader.Read(3) + 2;
                        int blockWidth = 1 << (int)sizeBits;
                        int blockHeight = 1 << (int)sizeBits;

                        break;
                    }

                    case WebPTransformType.ColorTransform:
                    {
                        // The first 3 bits of the color transform data contain the width and height of the image block in number of bits,
                        // just like the predictor transform:
                        uint sizeBits = bitReader.Read(3) + 2;
                        int blockWidth = 1 << (int)sizeBits;
                        int blockHeight = 1 << (int)sizeBits;
                        break;
                    }
                }

                numberOfTransformsPresent++;
                if (numberOfTransformsPresent == 4)
                {
                    break;
                }

                transformPresent = bitReader.ReadBit();
            }

            // Use the width and height from the VP8X information, if its provided, because its 3 bytes instead of 14 bits.
            bool isVpxDimensionsPresent = vpxHeight != 0 || vpxWidth != 0;

            return new WebPImageInfo()
                   {
                       Width = isVpxDimensionsPresent ? vpxWidth : (int)width,
                       Height = isVpxDimensionsPresent ? vpxHeight : (int)height,
                       IsLossLess = true,
                       DataSize = dataSize
                   };
        }

        private void ReadSimpleLossy<TPixel>(Buffer2D<TPixel> pixels, int width, int height)
            where TPixel : struct, IPixel<TPixel>
        {
            // TODO: implement decoding
        }

        private void ReadSimpleLossless<TPixel>(Buffer2D<TPixel> pixels, int width, int height)
            where TPixel : struct, IPixel<TPixel>
        {
            // TODO: implement decoding
        }

        private void ReadExtended<TPixel>(Buffer2D<TPixel> pixels, int width, int height)
            where TPixel : struct, IPixel<TPixel>
        {
            // TODO: implement decoding
        }
    }
}
