# SupercentVN Luna Tools

Luna Build Assistant (Tools/Luna/Build Assistant: Build Develop, Build & upload, pre-upload checks,
build size, asset settings), Luna cache cleaner and Playground cookie fix, shared by the luna_*
projects as a git submodule.

## Add to a project

From the repository root (the Unity project is at `unity_project/<project>`):

```sh
git submodule add https://github.com/SCVN-Luke/SupercentVN_LunaTools.git unity_project/<project>/Assets/SupercentVN/LunaTools
```

Delete any old copy under `Assets/SupercentVN/Tools/Editor` in the same step, before Unity
refreshes: the scripts keep their GUIDs, and Unity re-GUIDs one side if it sees both copies.
Commit the submodule, `.gitmodules` and `Assets/SupercentVN/LunaTools.meta`.

Cloning a project: `git clone --recurse-submodules …`, or `git submodule update --init` afterwards.

## Changing the tool

Edit it inside any project (`Assets/SupercentVN/LunaTools`), then commit and push **in the
submodule**, and commit the new submodule pointer in that project. Other projects get the change
with `git submodule update --remote Assets/SupercentVN/LunaTools` (then commit their pointer).
Keep fixes generic: a problem met in one project usually hits the others too.

The Editor asmdef stays `autoReferenced: false`: its `Supercent.Util.Editor` namespace would
otherwise break `: Editor` base classes in project code.
