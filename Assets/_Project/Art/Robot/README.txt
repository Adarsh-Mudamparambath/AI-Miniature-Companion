ROBOT GUIDE - UNITY PACKAGE
Files: Robot.fbx (mesh + rig + materials + face texture + 14 animations), FaceEmotions.png, RobotFace.cs

1. Copy all files into your Unity project's Assets folder.
2. Robot.fbx > Rig tab: Animation Type = Generic. Apply.
3. Robot.fbx > Animation tab: tick Loop Time on Idle, Walk, Beckon, Talk, Celebrate, Look_Around.
4. FaceEmotions.png: Filter Mode = Point, Compression = None, Wrap Mode = Clamp.
5. Robot.fbx > Materials tab > Extract Materials. On "FaceScreen": Base Map = FaceEmotions,
   Emission ON, Emission Map = FaceEmotions, Emission Color = white.
6. Drag Robot.fbx into the scene, add an Animator (with an Animator Controller holding the clips)
   and the RobotFace component.

Clips: Idle, Walk, Wave, Point_Right, Point_Left, Beckon, Welcome, Nod_Yes, Shake_No,
       Talk, Thinking, Celebrate, Bow, Look_Around
Emotions (RobotFace.emotionIndex / SetEmotion): 0 Neutral, 1 Happy, 2 Blink, 3 Sad,
       4 Angry, 5 Surprised, 6 Love, 7 Sleepy
