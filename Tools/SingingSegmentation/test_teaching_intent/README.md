# Voice teaching intent checks

Run from the project root:

```powershell
dotnet run --project Tools/SingingSegmentation/test_teaching_intent/TeachingIntentChecks.csproj --configuration Release
```

This standalone executable compiles the production `PersonalSingingLearningTeachingIntent.cs` directly. It does not load Unity, contact any service, read microphone audio, or save annotation labels. The current corpus has 81 assertions covering ordered speech/singing correction, explicit source seconds, whole-recording assertions, negation, partial/ambiguous wording, and unrelated singing requests.

The parser implements common, explicit Chinese teaching shortcuts. It is not a general language model. `review` means that the panel should show the grounded recording for manual inspection; it must not invent an interval. Every other mode is still a provisional draft, requiring the user's confirmation before the client saves training labels. The client is responsible for resolving the original recording and excluding the correction utterance itself; those lifecycle checks live in `PersonalSingingLearningTeachingRegression`.
