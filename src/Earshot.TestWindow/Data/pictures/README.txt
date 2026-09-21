These pictures are drawn schematics, not real screenshots: this repository is public, a real
capture would show the owner's other apps and his AirPods' own name, and Windows does not allow
its permission box to be captured at all. They are produced by
tests\Earshot.Tests\TestWindow\HowToPictureGenerator.cs; run its own test
(HowToPictureGeneratorTests.EveryGeneratedPictureMatchesTheCheckedInFileOnDisk) with
EARSHOT_REGENERATE_PICTURES=1 set to redraw and overwrite the files here, then commit the result
like any other source change.

Any of these can be replaced with a real screenshot later: save it as a PNG under this same file
name (for example earshot-icon-taskbar.png) and it is picked up exactly the same way, with no code
change. Keep each file under about 60 KB.
