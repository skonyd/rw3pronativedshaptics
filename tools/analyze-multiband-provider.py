"""Compare raw provider payloads without inventing an undocumented frequency decoder."""
import json
from pathlib import Path

root=Path("TestResults/multitone-20261108")
results=[]
for folder in ["provider-v3","provider-update","provider-generated"]:
    d=json.loads((root/folder/"provider-results.json").read_text(encoding="utf-8-sig"))
    cases=d["Cases"]
    def payload(row,active=True):
        data=bytes.fromhex(row["Frames"][-1])
        side=row["Side"] if active else 3-row["Side"]
        return data[1:21] if side==2 else data[21:41]
    repeatable=all(payload(r)==payload(next(x for x in cases if x["Name"]==r["Name"] and x["Side"]==r["Side"] and x["Repeat"]!=r["Repeat"])) for r in cases)
    other_constant=all(payload(r,False)==payload(next(x for x in cases if x["Name"]=="silence" and x["Side"]==r["Side"]),False) for r in cases)
    mixture_differs=all(payload(next(x for x in cases if x["Name"]=="80-plus-190" and x["Side"]==side and x["Repeat"]==1)) != payload(next(x for x in cases if x["Name"]==control and x["Side"]==side and x["Repeat"]==1)) for side in [1,2] for control in ["80","190","135-at-summed-level"])
    results.append(dict(Dataset=folder,Completed=d["Completed"],Cases=len(cases),RepeatedActivePayloadsMatch=repeatable,
        OppositePayloadUnchanged=other_constant,MixtureDiffersFromSingleAndAverageControls=mixture_differs))
summary=dict(PhysicalFidelityVerified=False,Results=results,
    Limit="41-byte frames split into the two previously observed 20-byte side regions; changing header omitted. No frequency field unpacking, waveform reconstruction, motor spectrum/phase inference or arbitrary packet packing.")
(root/"provider-summary.json").write_text(json.dumps(summary,indent=2),encoding="utf-8")
print(json.dumps(summary,indent=2))
