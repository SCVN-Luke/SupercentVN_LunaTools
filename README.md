# SupercentVN Luna Tools

One shared copy of the Luna Build Assistant for every luna_* project on this machine.

## Use in a project

Add to `Packages/manifest.json` (projects live at `Workspace/<project>/unity_project/<project>`):

```json
"com.supercentvn.lunatools": "file:../../../../Unity Packages/SupercentVN Luna Tools",
```

Remove any old copy under `Assets/SupercentVN/Tools/Editor` first: the scripts keep their GUIDs.

## Changing the tool

Edit it from any project (it shows under Packages/SupercentVN Luna Tools); every project that
references the package gets the change on its next compile. Commit in this repository, not in the
game project. Keep fixes generic: a problem met in one project usually hits the others too.

For someone without this folder: Window → Package Manager → + → Add package from tarball, with the
.tgz made by `UnityEditor.PackageManager.Client.Pack`.
