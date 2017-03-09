﻿using System;
using System.Drawing;
using Cloo;
using KelpNet.Common;
using KelpNet.Common.Functions;
using KelpNet.Common.Tools;

namespace KelpNet.Functions.Connections
{
    [Serializable]
    public class Convolution2D : NeedPreviousInputFunction
    {
        public NdArray W;
        public NdArray b;

        public NdArray gW;
        public NdArray gb;

        private readonly int _kWidth;
        private readonly int _kHeight;
        private readonly int _stride;
        private readonly int _padX;
        private readonly int _padY;

        public Convolution2D(int inputChannels, int outputChannels, int kSize, int stride = 1, int pad = 0, bool noBias = false, double[,,,] initialW = null, double[] initialb = null, string name = "Conv2D") : base(name, inputChannels, outputChannels)
        {
            this._kWidth = kSize;
            this._kHeight = kSize;
            this._stride = stride;
            this._padX = pad;
            this._padY = pad;

            this.Parameters = new FunctionParameter[noBias ? 1 : 2];

            this.Initialize(initialW, initialb);
        }

        public Convolution2D(int inputChannels, int outputChannels, Size kSize, int stride = 1, Size pad = new Size(), bool noBias = false, double[,,,] initialW = null, double[] initialb = null, string name = "Conv2D") : base(name, inputChannels, outputChannels)
        {
            if (pad == Size.Empty)
                pad = new Size(0, 0);

            this._kWidth = kSize.Width;
            this._kHeight = kSize.Height;
            this._stride = stride;
            this._padX = pad.Width;
            this._padY = pad.Height;

            this.Parameters = new FunctionParameter[noBias ? 1 : 2];

            this.Initialize(initialW, initialb);
        }

        void Initialize(double[,,,] initialW = null, double[] initialb = null)
        {
            this.W = NdArray.Zeros(OutputCount, InputCount, this._kHeight, this._kWidth);
            this.gW = NdArray.ZerosLike(this.W);

            if (initialW == null)
            {
                Initializer.InitWeight(this.W);
            }
            else
            {
                //サイズチェックを兼ねる
                Buffer.BlockCopy(initialW, 0, this.W.Data, 0, sizeof(double) * initialW.Length);
            }

            this.Parameters[0] = new FunctionParameter(this.W, this.gW, this.Name + " W");

            //noBias=trueでもbiasを用意して更新しない
            this.b = NdArray.Zeros(OutputCount);
            this.gb = NdArray.ZerosLike(this.b);

            if (this.Parameters.Length > 1)
            {
                if (initialb != null)
                {
                    Buffer.BlockCopy(initialb, 0, this.b.Data, 0, sizeof(double) * initialb.Length);
                }

                this.Parameters[1] = new FunctionParameter(this.b, this.gb, this.Name + " b");
            }

            //カーネルを作成
            if (IsGpu)
            {
                ForwardKernel = Weaver.CreateKernel(ForwardKernelSource, "Convolution2DForward");
                ForwardKernel.SetValueArgument(9, this._stride);
                ForwardKernel.SetValueArgument(10, this._padX);
                ForwardKernel.SetValueArgument(11, this._padY);
                ForwardKernel.SetValueArgument(12, this._kHeight);
                ForwardKernel.SetValueArgument(13, this._kWidth);
                ForwardKernel.SetValueArgument(14, this.OutputCount);
                ForwardKernel.SetValueArgument(15, this.InputCount);

                //BackwardKernel = Weaver.CreateKernel("", "");

            }
        }

        const string ForwardKernelSource =
@"
#pragma OPENCL EXTENSION cl_khr_fp64 : enable
__kernel void Convolution2DForward(
	__global double *gpuX,
	__global double *gpuW, 
	__global double *gpub, 
	__global double *gpuY,
    int inputShape1,
    int inputShape2,
    int inputLength,
    int outputWidth,
    int outputHeight,
    int stride,
	int padX,
	int padY,
	int kHeight,
	int kWidth,
	int OutputCount,
	int InputCount)
{
	int batchCounter = get_global_id(0);
	int och = get_global_id(1) / (outputHeight * outputWidth);
    int oy = (get_global_id(1) % (outputHeight * outputWidth)) / outputWidth;
    int ox = (get_global_id(1) % (outputHeight * outputWidth)) % outputWidth;

    int outChOffset = och * InputCount * kHeight * kWidth;

    int resultIndex = batchCounter * OutputCount * outputHeight * outputWidth + och * outputHeight * outputWidth + oy * outputWidth + ox;

    for (int ich = 0; ich < InputCount; ich++)
    {
        int inChOffset = ich * kHeight * kWidth;

        int inputOffset = ich * inputShape1 * inputShape2;

        for (int ky = 0; ky < kHeight; ky++)
        {
            int iy = oy * stride + ky - padY;

            if (iy >= 0 && iy < inputShape1)
            {
                for (int kx = 0; kx < kWidth; kx++)
                {
                    int ix = ox * stride + kx - padX;

                    if (ix >= 0 && ix < inputShape2)
                    {
                        int wIndex = outChOffset + inChOffset + ky * kWidth + kx;
                        int inputIndex = inputOffset + iy * inputShape2 + ix + batchCounter * inputLength;

                        gpuY[resultIndex] += gpuX[inputIndex] * gpuW[wIndex];
                    }
                }
            }
        }
    }

    gpuY[resultIndex] += gpub[och];
}";

        protected override BatchArray NeedPreviousForward(BatchArray input)
        {
            int outputHeight = (int)Math.Floor((input.Shape[1] - this._kHeight + this._padY * 2.0) / this._stride) + 1;
            int outputWidth = (int)Math.Floor((input.Shape[2] - this._kWidth + this._padX * 2.0) / this._stride) + 1;

            double[] result = new double[this.OutputCount * outputHeight * outputWidth * input.BatchCount];

            if (!IsGpu)
            {
                for (int batchCounter = 0; batchCounter < input.BatchCount; batchCounter++)
                {
                    int resultIndex = batchCounter * this.OutputCount * outputHeight * outputWidth;

                    for (int och = 0; och < this.OutputCount; och++)
                    {
                        //Wインデックス用
                        int outChOffset = och * this.InputCount * this._kHeight * this._kWidth;

                        for (int oy = 0; oy < outputHeight; oy++)
                        {
                            for (int ox = 0; ox < outputWidth; ox++)
                            {
                                for (int ich = 0; ich < this.InputCount; ich++)
                                {
                                    //Wインデックス用
                                    int inChOffset = ich * this._kHeight * this._kWidth;

                                    //inputインデックス用
                                    int inputOffset = ich * input.Shape[1] * input.Shape[2];

                                    for (int ky = 0; ky < this._kHeight; ky++)
                                    {
                                        int iy = oy * this._stride + ky - this._padY;

                                        if (iy >= 0 && iy < input.Shape[1])
                                        {
                                            for (int kx = 0; kx < this._kWidth; kx++)
                                            {
                                                int ix = ox * this._stride + kx - this._padX;

                                                if (ix >= 0 && ix < input.Shape[2])
                                                {
                                                    int wIndex = outChOffset + inChOffset + ky * this._kWidth + kx;
                                                    int inputIndex = inputOffset + iy * input.Shape[2] + ix + batchCounter * input.Length;

                                                    result[resultIndex] += input.Data[inputIndex] * this.W.Data[wIndex];
                                                }
                                            }
                                        }
                                    }
                                }

                                result[resultIndex] += this.b.Data[och];
                                resultIndex++;
                            }
                        }
                    }
                }
            }
            else
            {
                using (ComputeBuffer<double> gpuX = new ComputeBuffer<double>(Weaver.Context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, input.Data))
                using (ComputeBuffer<double> gpuW = new ComputeBuffer<double>(Weaver.Context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, this.W.Data))
                using (ComputeBuffer<double> gpub = new ComputeBuffer<double>(Weaver.Context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, this.b.Data))
                using (ComputeBuffer<double> gpuY = new ComputeBuffer<double>(Weaver.Context, ComputeMemoryFlags.ReadWrite | ComputeMemoryFlags.CopyHostPointer, result))
                {
                    ForwardKernel.SetMemoryArgument(0, gpuX);
                    ForwardKernel.SetMemoryArgument(1, gpuW);
                    ForwardKernel.SetMemoryArgument(2, gpub);
                    ForwardKernel.SetMemoryArgument(3, gpuY);
                    ForwardKernel.SetValueArgument(4, input.Shape[1]);
                    ForwardKernel.SetValueArgument(5, input.Shape[2]);
                    ForwardKernel.SetValueArgument(6, input.Length);
                    ForwardKernel.SetValueArgument(7, outputWidth);
                    ForwardKernel.SetValueArgument(8, outputHeight);

                    Weaver.CommandQueue.Execute
                    (
                        ForwardKernel,
                        null,
                        new long[] { input.BatchCount, OutputCount * outputHeight * outputWidth},
                        null,
                        null
                    );

                    Weaver.CommandQueue.ReadFromBuffer(gpuY, ref result, true, null);

                }
            }

            return BatchArray.Convert(result, new[] { this.OutputCount, outputHeight, outputWidth }, input.BatchCount);
        }

        protected override BatchArray NeedPreviousBackward(BatchArray gy, BatchArray prevInput)
        {
            double[] gx = new double[prevInput.Data.Length];

            for (int batchCounter = 0; batchCounter < gy.BatchCount; batchCounter++)
            {
                int gyIndex = batchCounter * gy.Length;

                for (int och = 0; och < gy.Shape[0]; och++)
                {
                    //gWインデックス用
                    int outChOffset = och * this.InputCount * this._kHeight * this._kWidth;

                    for (int oy = 0; oy < gy.Shape[1]; oy++)
                    {
                        for (int ox = 0; ox < gy.Shape[2]; ox++)
                        {
                            double gyData = gy.Data[gyIndex++]; //gyIndex = ch * ox * oy

                            for (int ich = 0; ich < prevInput.Shape[0]; ich++)
                            {
                                //gWインデックス用
                                int inChOffset = ich * this._kHeight * this._kWidth;

                                //inputインデックス用
                                int inputOffset = ich * prevInput.Shape[1] * prevInput.Shape[2];

                                for (int ky = 0; ky < this._kHeight; ky++)
                                {
                                    int iy = oy * this._stride + ky - this._padY;

                                    if (iy >= 0 && iy < prevInput.Shape[1])
                                    {
                                        for (int kx = 0; kx < this._kWidth; kx++)
                                        {
                                            int ix = ox * this._stride + kx - this._padX;

                                            if (ix >= 0 && ix < prevInput.Shape[2])
                                            {
                                                //WとgWのshapeは等しい
                                                int wIndex = outChOffset + inChOffset + ky * this._kWidth + kx;

                                                //prevInputとgxのshapeは等しい
                                                int inputIndex = inputOffset + iy * prevInput.Shape[2] + ix + batchCounter * prevInput.Length;

                                                this.gW.Data[wIndex] += prevInput.Data[inputIndex] * gyData;

                                                gx[inputIndex] += this.W.Data[wIndex] * gyData;
                                            }
                                        }
                                    }
                                }
                            }

                            this.gb.Data[och] += gyData;
                        }
                    }
                }
            }

            return BatchArray.Convert(gx, prevInput.Shape, prevInput.BatchCount);
        }
    }
}
