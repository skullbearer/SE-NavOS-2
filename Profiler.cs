using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript
{
    partial class Program
    {
        public sealed class Profiler
        {
            private readonly Program program;

            public double RunningAverageMs => runningAverage;
            private double AverageRuntimeMs
            { 
                get 
                {
                    double sum = runtimeCollection[0];
                    for (int i = 1; i < BufferSize; i++)
                    {
                        sum += runtimeCollection[i];
                    }
                    return (sum / BufferSize);
                }
            }
            /// <summary>Use <see cref="MaxRuntimeMsFast">MaxRuntimeMsFast</see> if performance is a major concern</summary>
            public double MaxRuntimeMs
            {
                get
                {
                    double max = runtimeCollection[0];
                    for (int i = 1; i < BufferSize; i++)
                    {
                        if (runtimeCollection[i] > max)
                        {
                            max = runtimeCollection[i];
                        }
                    }
                    return max;
                }
            }
            public double MaxRuntimeMsFast { get; private set; }
            public double MinRuntimeMs
            {
                get
                {
                    double min = runtimeCollection[0];
                    for (int i = 1; i < BufferSize; i++)
                    {
                        if (runtimeCollection[i] < min)
                        {
                            min = runtimeCollection[i];
                        }
                    }
                    return min;
                }
            }

            private readonly double[] runtimeCollection;
            private double runningAverage;
            private int counter = 0;
            public readonly int BufferSize;

            /// <summary></summary>
            /// <param name="Program">The Program instance of this script.</param>
            /// <param name="BufferSize">Runtime buffer size. Must be 1 or higher.</param>
            public Profiler(Program Program, int BufferSize = 300)
            {
                this.program = Program;
                this.MaxRuntimeMsFast = Program.Runtime.LastRunTimeMs;
                this.BufferSize = MathHelper.Clamp(BufferSize, 1, int.MaxValue);
                this.runtimeCollection = new double[this.BufferSize];
                this.runtimeCollection[counter] = Program.Runtime.LastRunTimeMs;
                this.counter++;
            }
            
            public void Run()
            {
                runningAverage -= runtimeCollection[counter] / BufferSize;
                runningAverage += program.Runtime.LastRunTimeMs / BufferSize;

                runtimeCollection[counter] = program.Runtime.LastRunTimeMs;

                if (program.Runtime.LastRunTimeMs > MaxRuntimeMsFast)
                {
                    MaxRuntimeMsFast = program.Runtime.LastRunTimeMs;
                }

                counter++;

                if (counter >= BufferSize)
                {
                    counter = 0;
                    runningAverage = AverageRuntimeMs;
                    MaxRuntimeMsFast = program.Runtime.LastRunTimeMs;
                }
            }
        }
    }
}
