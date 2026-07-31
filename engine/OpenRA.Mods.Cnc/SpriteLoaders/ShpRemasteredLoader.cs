#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ICSharpCode.SharpZipLib.Zip;
using OpenRA.Graphics;
using OpenRA.Mods.Common.SpriteLoaders;
using OpenRA.Primitives;

namespace OpenRA.Mods.Cnc.SpriteLoaders
{
	public class ShpRemasteredLoader : ISpriteLoader
	{
		public static bool IsShpRemastered(Stream s)
		{
			var start = s.Position;
			var isZipFile = s.ReadUInt32() == 0x04034B50;
			s.Position = start;

			return isZipFile;
		}

		public bool TryParseSprite(Stream s, string filename, out ISpriteFrame[] frames, out TypeDictionary metadata)
		{
			metadata = null;
			if (!IsShpRemastered(s))
			{
				frames = null;
				return false;
			}

			frames = new ShpRemasteredSprite(s).Frames.ToArray();
			return true;
		}
	}

	public class ShpRemasteredSprite
	{
		static readonly Regex FilenameRegex = new(@"^(?<prefix>.+?[\-_])(?<frame>\d{4})\.tga$");
		static readonly Regex MetaRegex = new(@"^\{""size"":\[(?<width>\d+),(?<height>\d+)\],""crop"":\[(?<left>\d+),(?<top>\d+),(?<right>\d+),(?<bottom>\d+)\]\}$");

		static int ParseGroup(Match match, string group)
		{
			return Exts.ParseInt32Invariant(match.Groups[group].Value);
		}

		public IReadOnlyList<ISpriteFrame> Frames { get; }

		public ShpRemasteredSprite(Stream stream)
		{
			var container = new ZipFile(stream);

			// Archives normally contain a single flat-numbered frame sequence
			// (e.g. "name-0000.tga" .. "name-NNNN.tga"), sharing one prefix.
			//
			// Some Remastered archives (e.g. units with a per-facing baked
			// turret/recoil animation) instead nest a second numeric group
			// per facing (e.g. "name-0032-0000.tga" .. "name-0032-0007.tga",
			// "name-0033-0000.tga", ...), which produces a distinct prefix
			// per facing under FilenameRegex.
			//
			// Rather than requiring a single global prefix, group frames by
			// their individual prefix, then concatenate the groups in sorted
			// order into one flat frame list. For archives with only one
			// prefix (the common case) this produces an identical result to
			// the previous single-prefix implementation. For multi-prefix
			// archives, groups are ordered ordinally by prefix string, which
			// places the base/flat group first (it's a strict string-prefix
			// of the nested ones) followed by nested groups in ascending
			// numeric order, since the embedded facing numbers are
			// fixed-width and therefore sort correctly as strings.
			var groups = new SortedDictionary<string, int>(StringComparer.Ordinal);

			foreach (ZipEntry entry in container)
			{
				var match = FilenameRegex.Match(entry.Name);
				if (!match.Success)
					continue;

				var prefix = match.Groups["prefix"].Value;
				var frameNumber = Exts.ParseInt32Invariant(match.Groups["frame"].Value);

				groups.TryGetValue(prefix, out var existingCount);
				groups[prefix] = Math.Max(existingCount, frameNumber + 1);
			}

			if (groups.Count == 0)
				throw new InvalidDataException("Archive does not contain any frames matching the expected naming pattern.");

			var totalFrameCount = groups.Values.Sum();
			var frames = new ISpriteFrame[totalFrameCount];

			var offset = 0;
			foreach (var group in groups)
			{
				var prefix = group.Key;
				var count = group.Value;

				for (var i = 0; i < count; i++)
				{
					var tgaEntry = container.GetEntry($"{prefix}{i:D4}.tga");

					// Blank frame
					if (tgaEntry == null)
					{
						frames[offset + i] = new TgaSprite.TgaFrame();
						continue;
					}

					var metaEntry = container.GetEntry($"{prefix}{i:D4}.meta");
					using (var tgaStream = container.GetInputStream(tgaEntry))
					{
						var metaStream = metaEntry != null ? container.GetInputStream(metaEntry) : null;
						if (metaStream != null)
						{
							string metaText;
							using (metaStream)
							using (var metaReader = new StreamReader(metaStream, bufferSize: 64))
								metaText = metaReader.ReadToEnd();

							var meta = MetaRegex.Match(metaText);
							var crop = Rectangle.FromLTRB(
								ParseGroup(meta, "left"), ParseGroup(meta, "top"),
								ParseGroup(meta, "right"), ParseGroup(meta, "bottom"));

							var frameSize = new Size(ParseGroup(meta, "width"), ParseGroup(meta, "height"));
							frames[offset + i] = new TgaSprite.TgaFrame(tgaStream, frameSize, crop);
						}
						else
							frames[offset + i] = new TgaSprite.TgaFrame(tgaStream);
					}
				}

				offset += count;
			}

			Frames = frames;
		}
	}
}
