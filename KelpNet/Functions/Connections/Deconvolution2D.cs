﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Cloo;
using KelpNet.Common;
using KelpNet.Common.Functions;
using KelpNet.Common.Tools;

namespace KelpNet.Functions.Connections
{
    [Serializable]
    public class Deconvolution2D : NeedPreviousInputFunction
    {
        const string FUNCTION_NAME = "Deconvolution2D";

        private readonly Activation _activation;
        private readonly List<BatchArray> _prevOutput = new List<BatchArray>();

        public NdArray W;
        public NdArray b;

        public NdArray gW;
        public NdArray gb;

        private int _kWidth;
        private int _kHeight;
        private int _subSample;
        private int _trimX;
        private int _trimY;

        public bool IsGpu;
        [NonSerialized]
        public ComputeKernel ForwardKernel;

        [NonSerialized]
        public ComputeKernel BackwardgWKernel;

        [NonSerialized]
        public ComputeKernel BackwardgXKernel;

        public Deconvolution2D(int inputChannels, int outputChannels, int kSize, int subSample = 1, int trim = 0, bool noBias = false, Real[,,,] initialW = null, Real[] initialb = null, string name = FUNCTION_NAME, bool isGpu = false, Activation activation = null) : base(name, inputChannels, outputChannels)
        {
            this._kWidth = kSize;
            this._kHeight = kSize;
            this._trimX = trim;
            this._trimY = trim;
            this._subSample = subSample;

            this.Parameters = new FunctionParameter[noBias ? 1 : 2];
            this._activation = activation;

            this.IsGpu = isGpu && Weaver.Enable;
            this.Initialize(initialW, initialb);
        }

        public Deconvolution2D(int inputChannels, int outputChannels, Size kSize, int subSample = 1, Size trim = new Size(), bool noBias = false, Real[,,,] initialW = null, Real[] initialb = null, string name = FUNCTION_NAME, bool isGpu = false, Activation activation = null) : base(name, inputChannels, outputChannels)
        {
            this._kWidth = kSize.Width;
            this._kHeight = kSize.Height;
            this._trimX = trim.Width;
            this._trimY = trim.Height;

            this._subSample = subSample;

            this.Parameters = new FunctionParameter[noBias ? 1 : 2];
            this._activation = activation;

            this.IsGpu = isGpu && Weaver.Enable;
            this.Initialize(initialW, initialb);
        }

        void Initialize(Real[,,,] initialW = null, Real[] initialb = null)
        {
            this.W = new NdArray(OutputCount, InputCount, this._kHeight, this._kWidth);
            this.gW = NdArray.ZerosLike(this.W);

            if (initialW == null)
            {
                Initializer.InitWeight(this.W);
            }
            else
            {
                this.W.Data = initialW.Cast<Real>().ToArray();
            }

            this.Parameters[0] = new FunctionParameter(this.W, this.gW, this.Name + " W");

            //noBias=trueでもbiasを用意して更新しない
            this.b = new NdArray(OutputCount);
            this.gb = NdArray.ZerosLike(this.b);

            if (this.Parameters.Length > 1)
            {
                if (initialb != null)
                {
                    this.b.Data = initialb.ToArray();
                }

                this.Parameters[1] = new FunctionParameter(this.b, this.gb, this.Name + " b");
            }

            if (IsGpu)
            {
                var KernelSource = Weaver.GetKernelSource(FUNCTION_NAME);

                if (this._activation != null)
                {
                    KernelSource = this._activation.ActivateFunctionString + KernelSource.Replace("/*ForwardActivate*/", "ForwardActivate(gpuY + outputIndex);");
                }

                var program = Weaver.CreateProgram(KernelSource);
                this.ForwardKernel = program.CreateKernel("Deconvolution2DForward");
                this.BackwardgWKernel = program.CreateKernel("Convolution2DgWBackward");
                this.BackwardgXKernel = program.CreateKernel("Convolution2DgXBackward");
            }
        }

        protected override BatchArray NeedPreviousForward(BatchArray input)
        {
            int outputHeight = (input.Shape[1] - 1) * this._subSample + this._kHeight - this._trimY * 2;
            int outputWidth = (input.Shape[2] - 1) * this._subSample + this._kWidth - this._trimX * 2;

            Real[] result = new Real[input.BatchCount * this.OutputCount * outputWidth * outputHeight];

            int outSizeOffset = outputWidth * outputHeight;

            int inputSizeOffset = input.Shape[1] * input.Shape[2];
            int kSizeOffset = this.W.Shape[2] * this.W.Shape[3];

            if (!IsGpu)
            {
                for (int batchCount = 0; batchCount < input.BatchCount; batchCount++)
                {
                    for (int och = 0; och < this.OutputCount; och++)
                    {
                        for (int oy = this._trimY; oy < outputHeight + this._trimY; oy++)
                        {
                            int iyLimit = oy / this._subSample + 1 < input.Shape[1] ? oy / this._subSample + 1 : input.Shape[1];
                            int iyStart = oy - this.W.Shape[2] < 0 ? 0 : (oy - this.W.Shape[2]) / this._subSample + 1;

                            for (int ox = this._trimX; ox < outputWidth + this._trimX; ox++)
                            {
                                int ixLimit = ox / this._subSample + 1 < input.Shape[2] ? ox / this._subSample + 1 : input.Shape[2];
                                int ixStart = ox - this.W.Shape[3] < 0 ? 0 : (ox - this.W.Shape[3]) / this._subSample + 1;

                                int outputIndex = batchCount * this.OutputCount * outSizeOffset + och * outSizeOffset + (oy - this._trimY) * outputWidth + ox - this._trimX;

                                for (int ich = 0; ich < input.Shape[0]; ich++)
                                {
                                    int inputIndexOffset = batchCount * input.Length + ich * inputSizeOffset;
                                    int kernelIndexOffset = och * this.W.Shape[1] * kSizeOffset + ich * kSizeOffset;

                                    for (int iy = iyStart; iy < iyLimit; iy++)
                                    {
                                        for (int ix = ixStart; ix < ixLimit; ix++)
                                        {
                                            int inputIndex = inputIndexOffset + iy * input.Shape[2] + ix;
                                            int kernelIndex = kernelIndexOffset + (oy - iy * this._subSample) * this.W.Shape[3] + (ox - ix * this._subSample);

                                            result[outputIndex] += input.Data[inputIndex] * this.W.Data[kernelIndex];
                                        }
                                    }
                                }

                                result[outputIndex] += this.b.Data[och];
                                if (this._activation != null) this._activation.ForwardActivate(ref result[outputIndex]);
                            }
                        }
                    }
                }

            }
            else
            {
                using (ComputeBuffer<Real> gpuX = new ComputeBuffer<Real>(Weaver.Context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, input.Data))
                using (ComputeBuffer<Real> gpuW = new ComputeBuffer<Real>(Weaver.Context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, this.W.Data))
                using (ComputeBuffer<Real> gpub = new ComputeBuffer<Real>(Weaver.Context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, this.b.Data))
                using (ComputeBuffer<Real> gpuY = new ComputeBuffer<Real>(Weaver.Context, ComputeMemoryFlags.WriteOnly | ComputeMemoryFlags.AllocateHostPointer, result.Length))
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
                    ForwardKernel.SetValueArgument(9, this._subSample);
                    ForwardKernel.SetValueArgument(10, this._trimX);
                    ForwardKernel.SetValueArgument(11, this._trimY);
                    ForwardKernel.SetValueArgument(12, this._kHeight);
                    ForwardKernel.SetValueArgument(13, this._kWidth);
                    ForwardKernel.SetValueArgument(14, this.OutputCount);
                    ForwardKernel.SetValueArgument(15, this.InputCount);

                    Weaver.CommandQueue.Execute
                        (
                            ForwardKernel,
                            null,
                            new long[] { input.BatchCount * OutputCount, outputHeight, outputWidth },
                            null,
                            null
                        );

                    Weaver.CommandQueue.Finish();
                    Weaver.CommandQueue.ReadFromBuffer(gpuY, ref result, true, null);
                }

            }

            BatchArray output = BatchArray.Convert(result, new[] { this.OutputCount, outputHeight, outputWidth }, input.BatchCount);

            if (this._activation != null)
            {
                this._prevOutput.Add(output);
            }

            return output;
        }

        protected override BatchArray NeedPreviousBackward(BatchArray gy, BatchArray x)
        {
            Real[] prevOutputData = new Real[gy.Data.Length];

            if (this._activation != null)
            {
                prevOutputData = this._prevOutput[this._prevOutput.Count - 1].Data;
                this._prevOutput.RemoveAt(this._prevOutput.Count - 1);
            }

            Real[] gx = new Real[x.Data.Length];
            Real[] gw = new Real[this.gW.Data.Length];
            Array.Copy(this.gW.Data, gw, gw.Length);

            Real[] activatedgy = new Real[gy.BatchCount * gy.Length];

            for (int batchCounter = 0; batchCounter < gy.BatchCount; batchCounter++)
            {
                for (int och = 0; och < gy.Shape[0]; och++)
                {
                    for (int oy = 0; oy < gy.Shape[1]; oy++)
                    {
                        for (int ox = 0; ox < gy.Shape[2]; ox++)
                        {
                            int gyIndex = batchCounter * gy.Length + och * gy.Shape[1] * gy.Shape[2] + oy * gy.Shape[2] + ox;
                            Real gyData = gy.Data[gyIndex];

                            if (this._activation != null)
                            {
                                this._activation.BackwardActivate(ref gyData, prevOutputData[gyIndex]);
                            }

                            activatedgy[batchCounter * gy.Length + och * gy.Shape[1] * gy.Shape[2] + oy * gy.Shape[2] + ox] = gyData;

                            this.gb.Data[och] += gyData;
                        }
                    }
                }
            }

            if (!IsGpu)
            {
                //本来のロジック
                for (int batchCount = 0; batchCount < gy.BatchCount; batchCount++)
                {
                    for (int och = 0; och < OutputCount; och++)
                    {
                        int outChOffset = och * this.gW.Shape[1] * this.gW.Shape[2] * this.gW.Shape[3];
                        int inputOffset = och * gy.Shape[1] * gy.Shape[2];

                        for (int oy = this._trimY; oy < gy.Shape[1] + this._trimY; oy++)
                        {
                            int iyLimit = oy / this._subSample + 1 < x.Shape[1] ? oy / this._subSample + 1 : x.Shape[1];
                            int iyStart = oy - this.W.Shape[2] < 0 ? 0 : (oy - this.W.Shape[2]) / this._subSample + 1;

                            for (int ox = this._trimX; ox < gy.Shape[2] + this._trimX; ox++)
                            {
                                int ixLimit = ox / this._subSample + 1 < x.Shape[2] ? ox / this._subSample + 1 : x.Shape[2];
                                int ixStart = ox - this.W.Shape[3] < 0 ? 0 : (ox - this.W.Shape[3]) / this._subSample + 1;

                                int gyIndex = batchCount * gy.Length + inputOffset + (oy - this._trimY) * gy.Shape[2] + ox - this._trimX;
                                Real gyData = activatedgy[gyIndex];

                                for (int ich = 0; ich < InputCount; ich++)
                                {
                                    int inChOffset = outChOffset + ich * this.gW.Shape[2] * this.gW.Shape[3];
                                    int pinputOffset = batchCount * x.Length + ich * x.Shape[1] * x.Shape[2];

                                    for (int iy = iyStart; iy < iyLimit; iy++)
                                    {
                                        for (int ix = ixStart; ix < ixLimit; ix++)
                                        {
                                            int pInIndex = pinputOffset + iy * x.Shape[2] + ix;
                                            int gwIndex = inChOffset + (oy - iy * this._subSample) * this.gW.Shape[3] + (ox - ix * this._subSample);

                                            gw[gwIndex] += x.Data[pInIndex] * gyData;
                                            gx[pInIndex] += this.W.Data[gwIndex] * gyData;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                this.gW.Data = gw;
            }
            else
            {
                //gyは共通で使用
                using (ComputeBuffer<Real> gpugY = new ComputeBuffer<Real>(Weaver.Context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, activatedgy))
                {
                    using (ComputeBuffer<Real> gpugW = new ComputeBuffer<Real>(Weaver.Context, ComputeMemoryFlags.ReadWrite | ComputeMemoryFlags.CopyHostPointer, this.gW.Data))
                    using (ComputeBuffer<Real> gpuX = new ComputeBuffer<Real>(Weaver.Context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, x.Data))
                    {
                        this.BackwardgWKernel.SetMemoryArgument(0, gpugY);
                        this.BackwardgWKernel.SetMemoryArgument(1, gpuX);
                        this.BackwardgWKernel.SetMemoryArgument(2, gpugW);
                        this.BackwardgWKernel.SetValueArgument(3, gy.BatchCount);
                        this.BackwardgWKernel.SetValueArgument(4, this.InputCount);
                        this.BackwardgWKernel.SetValueArgument(5, gy.Length);
                        this.BackwardgWKernel.SetValueArgument(6, gy.Shape[1]);
                        this.BackwardgWKernel.SetValueArgument(7, gy.Shape[2]);
                        this.BackwardgWKernel.SetValueArgument(8, x.Shape[1]);
                        this.BackwardgWKernel.SetValueArgument(9, x.Shape[2]);
                        this.BackwardgWKernel.SetValueArgument(10, x.Length);
                        this.BackwardgWKernel.SetValueArgument(11, this._subSample);
                        this.BackwardgWKernel.SetValueArgument(12, this._trimX);
                        this.BackwardgWKernel.SetValueArgument(13, this._trimY);
                        this.BackwardgWKernel.SetValueArgument(14, this._kHeight);
                        this.BackwardgWKernel.SetValueArgument(15, this._kWidth);

                        Weaver.CommandQueue.Execute
                        (
                            this.BackwardgWKernel,
                            null,
                            new long[] { OutputCount * InputCount, this._kHeight, this._kWidth },
                            null,
                            null
                        );

                        Weaver.CommandQueue.Finish();
                        Weaver.CommandQueue.ReadFromBuffer(gpugW, ref this.gW.Data, true, null);
                    }

                    using (ComputeBuffer<Real> gpugX = new ComputeBuffer<Real>(Weaver.Context, ComputeMemoryFlags.WriteOnly | ComputeMemoryFlags.AllocateHostPointer, gx.Length))
                    using (ComputeBuffer<Real> gpuW = new ComputeBuffer<Real>(Weaver.Context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, this.W.Data))
                    {
                        this.BackwardgXKernel.SetMemoryArgument(0, gpugY);
                        this.BackwardgXKernel.SetMemoryArgument(1, gpuW);
                        this.BackwardgXKernel.SetMemoryArgument(2, gpugX);
                        this.BackwardgXKernel.SetValueArgument(3, this.OutputCount);
                        this.BackwardgXKernel.SetValueArgument(4, this.InputCount);
                        this.BackwardgXKernel.SetValueArgument(5, gy.Length);
                        this.BackwardgXKernel.SetValueArgument(6, gy.Shape[1]);
                        this.BackwardgXKernel.SetValueArgument(7, gy.Shape[2]);
                        this.BackwardgXKernel.SetValueArgument(8, x.Shape[1]);
                        this.BackwardgXKernel.SetValueArgument(9, x.Shape[2]);
                        this.BackwardgXKernel.SetValueArgument(10, x.Length);
                        this.BackwardgXKernel.SetValueArgument(11, this._subSample);
                        this.BackwardgXKernel.SetValueArgument(12, this._trimX);
                        this.BackwardgXKernel.SetValueArgument(13, this._trimY);
                        this.BackwardgXKernel.SetValueArgument(14, this._kHeight);
                        this.BackwardgXKernel.SetValueArgument(15, this._kWidth);

                        Weaver.CommandQueue.Execute
                        (
                            this.BackwardgXKernel,
                            null,
                            new long[] { gy.BatchCount * x.Shape[0], x.Shape[1], x.Shape[2] },
                            null,
                            null
                        );

                        Weaver.CommandQueue.Finish();
                        Weaver.CommandQueue.ReadFromBuffer(gpugX, ref gx, true, null);
                    }
                }
            }

            return BatchArray.Convert(gx, x.Shape, x.BatchCount);
        }
    }
}
