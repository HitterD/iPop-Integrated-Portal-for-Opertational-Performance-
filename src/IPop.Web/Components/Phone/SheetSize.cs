namespace IPop.Web.Components.Phone;

/// Bottom sheet heights: content height, about half, about 90 %, full screen.
public enum SheetSize { Auto, Half, Tall, Full }

/// One SegmentedControl option. Count shows as a small number after the label.
public sealed record SegmentItem(string Value, string Label, int? Count = null);
