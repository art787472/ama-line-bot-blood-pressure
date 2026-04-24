namespace BloodPressureBot.Models;

public record BloodPressureRecord(
    Guid Id,
    string UserId,
    int Systolic,
    int Diastolic,
    int? Pulse,
    DateTime MeasuredAt,
    DateTime CreatedAt);

public record BloodPressureReading(int Systolic, int Diastolic, int? Pulse);

public record Stats(
    int Count,
    double? AvgSystolic,
    double? AvgDiastolic,
    double? AvgPulse,
    int? MaxSystolic,
    int? MinSystolic);
