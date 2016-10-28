﻿using System;
using System.Linq;
using KelpNet.Common;
#if !DEBUG
using System.Threading.Tasks;
#endif

namespace KelpNet.Loss
{
    public partial class LossFunctions
    {
        public static NdArray MeanSquaredError(NdArray input, NdArray teachSignal, out double loss)
        {
            loss = 0.0;

            double[] diff = new double[teachSignal.Length];
            double coeff = 2.0 / diff.Length;

            for (int i = 0; i < input.Length; i++)
            {
                diff[i] = input.Data[i] - teachSignal.Data[i];
                loss += Math.Pow(diff[i], 2);

                diff[i] *= coeff;
            }

            loss /= diff.Length;

            return new NdArray(diff, teachSignal.Shape);
        }

        public static NdArray MeanSquaredError(NdArray input, Array teach, out double loss)
        {            
            return MeanSquaredError(input, NdArray.FromArray(teach),out loss);
        }

        public static NdArray[] MeanSquaredError(NdArray[] input, NdArray[] teachSignal, out double loss)
        {
            double[] lossList = new double[input.Length];
            NdArray[] result = new NdArray[input.Length];

#if DEBUG
            for (int i = 0; i < input.Length; i++)
#else
            Parallel.For(0, input.Length, i =>
#endif
            {
                result[i] = MeanSquaredError(input[i], teachSignal[i], out lossList[i]);
            }

#if !DEBUG
            );
#endif
            loss = lossList.Average();

            return result;
        }

        public static NdArray[] MeanSquaredError(NdArray[] input, Array[] teachSignal, out double loss)
        {
            double[] lossList = new double[input.Length];
            NdArray[] result = new NdArray[input.Length];

#if DEBUG
            for (int i = 0; i < input.Length; i++)
#else
            Parallel.For(0, input.Length, i =>
#endif
            {
                result[i] = MeanSquaredError(input[i], teachSignal[i], out lossList[i]);
            }

#if !DEBUG
            );
#endif
            loss = lossList.Average();

            return result;
        }
    }
}

