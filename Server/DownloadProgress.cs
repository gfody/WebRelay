using System;
using System.Collections.Generic;

namespace WebRelay
{
	public class DownloadProgress
	{
		private class Range : IComparable<Range>
		{
			public long Low;
			public long High;
			public long Length { get { return High - Low; } }

			public Range(long Low, long High)
			{
				this.Low = Low;
				this.High = High;
			}

			public int CompareTo(Range other)
			{
				if (Low < other.Low && High < other.Low)
					return -1;
				else if (Low > other.High)
					return 1;
				else
					return 0;
			}

			public long Merge(Range other)
			{
				long len = High - Low;

				if (other.Low < Low)
					Low = other.Low;

				if (other.High > High)
					High = other.High;

				return (High - Low) - len;
			}
		}

		private List<Range> ranges = new List<Range>();

		public long HighWaterMark { get { return ranges.Count > 0 ? ranges[ranges.Count - 1].High : 0; } }

		public long Downloaded { get; private set; }

		public long DownloadedBetween(long offset, long count)
		{
			lock (ranges)
			{
				var range = new Range(offset, offset + count);
				int i = ranges.BinarySearch(range);
				if (i > -1)
				{
					while (i > 0 && range.CompareTo(ranges[i - 1]) == 0)
						i--;

					long delta = ranges[i].Length - range.Merge(ranges[i]);
					while (i < ranges.Count - 1 && range.CompareTo(ranges[i + 1]) == 0)
					{
						delta += ranges[i + 1].Length - range.Merge(ranges[i + 1]);
						i++;
					}

					return delta;
				}
				else
					return 0;
			}
		}

		public void Download(long offset, long count)
		{
			lock (ranges)
			{
				var range = new Range(offset, offset + count);
				long delta = count;
				int i = ranges.BinarySearch(range);
				if (i > -1)
				{
					while (i > 0 && range.CompareTo(ranges[i - 1]) == 0)
						i--;

					delta = ranges[i].Merge(range);
					while (i < ranges.Count - 1 && ranges[i].CompareTo(ranges[i + 1]) == 0)
					{
						delta += ranges[i].Merge(ranges[i + 1]) - ranges[i + 1].Length;
						ranges.RemoveAt(i + 1);
					}
				}
				else
					ranges.Insert(~i, range);

				Downloaded += delta;
			}
		}

		public void Reset()
		{
			lock (ranges)
			{
				ranges.Clear();
				Downloaded = 0;
			}
		}
	}
}
