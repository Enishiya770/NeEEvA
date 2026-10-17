using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// Exact producer-instruction revisions only. Hash matching is equality, not a
// prefix/keyword rule; any changed or custom limitation remains verbatim.
public static class PrivateInterestScopePolicy
{
    static readonly Dictionary<string,string> Compact=new Dictionary<string,string>{
        ["D3EAC7EA660A7861"]="Selected observed memory only; not recognition, sight or permission. Omitted memory is recallable; task constraints and outcomes remain authoritative.",
        ["42C5C1657B40F9DC"]="Private playback-overlap review only; cannot speak, execute, mutate memory or prove delivery. Next action requires fresh checks.",
        ["4EA03502D2ED1F95"]="Unsampled intervals/pauses are unknown. Private thinking is intermittent. Silence, looking, approach or rest are optional.",
        ["FF97BEE2B5E7F42C"]="Past event only. Preserve request, operation, entity and time; null is unknown. No current sight/state or permission. Later events supersede state, not occurrence.",
        ["F892F916E35C2618"]="Actual step ledger. Pending predecessors block successors; speech is not completion. Historical receipts grant no current action.",
        ["7B3C2AA5D93B503B"]="Historical entity match only. Scene label cannot replace user constraints or prove current visibility.",
        ["57C43609F3363187"]="Same-entity fresh runtime readback; live reads only invalidate saved proof. No brightness, whole-room darkness, touch or new sight proof.",
        ["B05ABD86A273E1D8"]="Reviewed objective with bounded attempts. Arrival/history are not device completion. Failures do not prove absence. Fresh target match and opaque confirmation still required; memory grants no action.",
        ["D6411AD370950531"]="Search only. found requires fresh independent same-entity confirmation. Coverage concerns sampled views/reviewed pages, never all/occluded objects. Failure does not prove absence. Memory grants no sight/navigation/device authority. No device write; motion is one substep.",
        ["06ECF6949BEE42C1"]="Registered emission/Light state only; materials/baked light unchanged. Visual confirmation is separate.",
        ["06C822FE46B006FE"]="Current USER target revision only. Assessment is geometry, not execution. no-attempt means no submitted approach for this revision; targetKind distinguishes scene tasks.",
        ["D9C40CABEAB20DE5"]="USER geometry only; not object/view assessment. Reachable does not validate duration, service, pose history or execution.",
        ["5665AC35873B543C"]="Past movement for this targetKind/action only. Do not transfer failure across targets. USER assessment and actual scene-target result are separate.",
        ["8C7D9F9C553FCDF1"]="Actual floor station/facing only; not semantic identity, touching, fresh/better sight or user proximity. motionKind identifies relative observation offsets.",
        ["EE5AF6A2AD64728C"]="USER approach/follow assessment only; user tracking and support differ. Unsupported user pose does not forbid looking/object approach. Match each result to its targetKind/action.",
        ["9B7DD39B61C0349E"]="Match current execution; old dispatch/text cannot override arrival. Later changes do not undo completed events. New autonomous intent needs its own decision/identity; unfinished answers remain due.",
        ["AF1838DE5B01BBEA"]="This attempt only. false operationAttempted=not dispatched; null=unknown. Search/readiness is not a write; write alone may lack final proof. No prior/current unseen-state claim.",
        ["24FCF76C98373618"]="Earlier step only; operationSummary/lastAction describe that event, not current sight/state/retry permission. Null is unknown. Movement outcomes are separate.",
        ["420A2B75B3A6B630"]="Accepted text is not completed audio. Use actual timestamps/dialogue; no inferred long silence from lack of a command.",
        ["D2C04AA744A8D50E"]="Private opportunity only. Keep complete requests, corrections, obligations and constraints. Memory is historical, not current sight/proof/authority. Omitted catalogs/geometry remain recallable by fresh planning; omission proves neither absence nor completion.",
        ["5036B3633281B0A6"]="Playback-boundary edit only; no action, model request or audible-playback proof. Earlier played words remain history.",
        ["A57F819387C60023"]="Cleared buffer is not unheard speech. completedPlaybackText is actual audio, superseding drafts; drained is neither action completion nor human audibility. Already reported requirements do not reopen work; unfinished steps/independent answers remain due.",
        ["E4C06227C87C5409"]="Completed chunks for this result version only; offsets index completedPlaybackText. Full rows remain in runtime. Not every phrase describes this result.",
    };
    public static void Project(JObject root)
    {
        foreach(var field in root.Descendants().OfType<JProperty>().Where(p=>p.Name=="scope"||p.Name=="audioScope").ToArray())
            if(field.Value.Type==JTokenType.String && Compact.TryGetValue(ActivityEvidencePolicy.Stamp(field.Value),out string text))field.Value=text;
    }
}
