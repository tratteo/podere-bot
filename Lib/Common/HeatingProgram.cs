using System.Text;
using System.Text.Json.Serialization;

namespace PodereBot.Lib.Common;

public class HeatingProgram()
{
    public bool IsSuspended { get; set; } = false;

    [JsonInclude]
    public List<HeatingInterval> Intervals { get; private set; } = [];

    private HeatingProgram(IEnumerable<HeatingInterval> intervals)
        : this()
    {
        Intervals = intervals.ToList();
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        Intervals.ForEach(i => builder.AppendLine(i.ToString()));
        var duration = TimeSpan.FromSeconds(Intervals.Sum(i => i.ToTimestamp - i.FromTimestamp));
        builder.Append($"Tempo in attività: {duration.Hours}h {duration.Minutes}m");
        return builder.ToString();
    }

    public string ToCodeString()
    {
        return string.Join("/", Intervals.ConvertAll(i => i.ToCodeString()));
    }

    public HeatingInterval? GetNextInterval()
    {
        var date = DateTime.Now;
        var dayTimestamp = date.Hour * 3600 + date.Minute * 60;
        return Intervals.Where(i => i.FromTimestamp > dayTimestamp).MinBy(i => i.FromTimestamp - dayTimestamp);
    }

    public HeatingInterval? GetCurrentInterval()
    {
        var date = DateTime.Now;
        var dayTimestamp = date.Hour * 3600 + date.Minute * 60;
        return Intervals.FirstOrDefault(i => i.FromTimestamp <= dayTimestamp && i.ToTimestamp > dayTimestamp);
    }

    public static bool TryBuild(IEnumerable<HeatingInterval> intervals, out HeatingProgram? program)
    {
        foreach (var interval in intervals)
        {
            if (interval.FromTimestamp >= interval.ToTimestamp)
            {
                program = null;
                return false;
            }
        }

        if (intervals.Count() > 1)
        {
            for (int i = 0; i < intervals.Count(); i++)
            {
                var first = intervals.ElementAt(i);
                for (int j = i + 1; j < intervals.Count(); j++)
                {
                    var second = intervals.ElementAt(j);
                    if ((second.HoursFrom < first.HoursTo) || (second.HoursFrom == first.HoursTo && second.MinutesFrom < first.MinutesTo))
                    {
                        program = null;
                        return false;
                    }
                }
            }
        }

        program = new HeatingProgram(intervals);
        return true;
    }
}
