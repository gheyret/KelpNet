﻿using System;
using KelpNet.Common;
using KelpNet.Common.Tools;
using KelpNet.Functions;
using KelpNet.Functions.Activations;
using KelpNet.Functions.Connections;
using KelpNet.Functions.Normalization;
using KelpNet.Loss;
using KelpNet.Optimizers;

namespace KelpNetTester.Tests
{
    //Decoupled Neural Interfaces using Synthetic GradientsによるMNIST（手書き文字）の学習
    // http://ralo23.hatenablog.com/entry/2016/10/22/233405
    class Test11
    {
        //ミニバッチの数
        const int BATCH_DATA_COUNT = 200;

        //一世代あたりの訓練回数
        const int TRAIN_DATA_COUNT = 300; // = 60000 / 20

        //性能評価時のデータ数
        const int TEST_DATA_COUNT = 1000;

        public static void Run()
        {
            //MNISTのデータを用意する
            Console.WriteLine("MNIST Data Loading...");
            MnistData mnistData = new MnistData();


            Console.WriteLine("Training Start...");

            //ネットワークの構成を FunctionStack に書き連ねる
            FunctionStack Layer1 = new FunctionStack(
                new Linear(28 * 28, 256, name: "l1 Linear"),
                new BatchNormalization(256, name: "l1 Norm"),
                new ReLU(name: "l1 ReLU")
            );

            FunctionStack Layer2 = new FunctionStack(
                new Linear(256, 256, name: "l2 Linear"),
                new BatchNormalization(256, name: "l2 Norm"),
                new ReLU(name: "l2 ReLU")
            );

            FunctionStack Layer3 = new FunctionStack(
                new Linear(256, 256, name: "l3 Linear"),
                new BatchNormalization(256, name: "l3 Norm"),
                new ReLU(name: "l3 ReLU")
            );

            FunctionStack Layer4 = new FunctionStack(
                new Linear(256, 10, name: "l4 Linear")
            );

            //FunctionStack自身もFunctionとして積み上げられる
            FunctionStack nn = new FunctionStack
            (
                Layer1,
                Layer2,
                Layer3,
                Layer4
            );

            FunctionStack DNI1 = new FunctionStack(
                new Linear(256, 1024, name: "DNI1 Linear1"),
                new BatchNormalization(1024, name: "DNI1 Nrom1"),
                new ReLU(name: "DNI1 ReLU1"),
                new Linear(1024, 1024, name: "DNI1 Linear2"),
                new BatchNormalization(1024, name: "DNI1 Nrom2"),
                new ReLU(name: "DNI1 ReLU2"),
                new Linear(1024, 256, initialW: new Real[1024, 256], name: "DNI1 Linear3")
            );

            FunctionStack DNI2 = new FunctionStack(
                new Linear(256, 1024, name: "DNI2 Linear1"),
                new BatchNormalization(1024, name: "DNI2 Nrom1"),
                new ReLU(name: "DNI2 ReLU1"),
                new Linear(1024, 1024, name: "DNI2 Linear2"),
                new BatchNormalization(1024, name: "DNI2 Nrom2"),
                new ReLU(name: "DNI2 ReLU2"),
                new Linear(1024, 256, initialW: new Real[1024, 256], name: "DNI2 Linear3")
            );

            FunctionStack DNI3 = new FunctionStack(
                new Linear(256, 1024, name: "DNI3 Linear1"),
                new BatchNormalization(1024, name: "DNI3 Nrom1"),
                new ReLU(name: "DNI3 ReLU1"),
                new Linear(1024, 1024, name: "DNI3 Linear2"),
                new BatchNormalization(1024, name: "DNI3 Nrom2"),
                new ReLU(name: "DNI3 ReLU2"),
                new Linear(1024, 256, initialW: new Real[1024, 256], name: "DNI3 Linear3")
            );

            //optimizerを宣言
            Layer1.SetOptimizer(new Adam());
            Layer2.SetOptimizer(new Adam());
            Layer3.SetOptimizer(new Adam());
            Layer4.SetOptimizer(new Adam());

            DNI1.SetOptimizer(new Adam());
            DNI2.SetOptimizer(new Adam());
            DNI3.SetOptimizer(new Adam());

            //三世代学習
            for (int epoch = 0; epoch < 20; epoch++)
            {
                Console.WriteLine("epoch " + (epoch + 1));

                //全体での誤差を集計
                //List<Real> totalLoss = new List<Real>();

                //List<Real> DNI1totalLoss = new List<Real>();

                //List<Real> DNI2totalLoss = new List<Real>();

                //List<Real> DNI3totalLoss = new List<Real>();

                Real totalLoss = 0;
                Real DNI1totalLoss = 0;
                Real DNI2totalLoss = 0;
                Real DNI3totalLoss = 0;

                long totalLossCount = 0;
                long DNI1totalLossCount = 0;
                long DNI2totalLossCount = 0;
                long DNI3totalLossCount = 0;

                //何回バッチを実行するか
                for (int i = 1; i < TRAIN_DATA_COUNT + 1; i++)
                {
                    //訓練データからランダムにデータを取得
                    MnistDataSet datasetX = mnistData.GetRandomXSet(BATCH_DATA_COUNT);

                    //第一層を実行
                    BatchArray layer1ForwardResult = Layer1.Forward(datasetX.Data);

                    //第一層の傾きを取得
                    BatchArray DNI1Result = DNI1.Forward(layer1ForwardResult);

                    //第一層を更新
                    Layer1.Backward(DNI1Result);
                    Layer1.Update();

                    //第二層を実行
                    BatchArray layer2ForwardResult = Layer2.Forward(layer1ForwardResult);

                    //第二層の傾きを取得
                    BatchArray DNI2Result = DNI2.Forward(layer2ForwardResult);

                    //第二層を更新
                    BatchArray layer2BackwardResult = Layer2.Backward(DNI2Result);
                    Layer2.Update();

                    //第一層用のDNIの学習を実行
                    Real DNI1loss;
                    BatchArray DNI1lossResult = new MeanSquaredError().Evaluate(DNI1Result, layer2BackwardResult, out DNI1loss);
                    DNI1.Backward(DNI1lossResult);
                    DNI1.Update();
                    DNI1totalLoss += DNI1loss;
                    DNI1totalLossCount++;

                    //第二層を実行
                    BatchArray layer3ForwardResult = Layer3.Forward(layer2ForwardResult);

                    //第三層の傾きを取得
                    BatchArray DNI3Result = DNI3.Forward(layer3ForwardResult);

                    //第三層を更新
                    BatchArray layer3BackwardResult = Layer3.Backward(DNI3Result);
                    Layer3.Update();

                    //第二層用のDNIの学習を実行
                    Real DNI2loss;
                    BatchArray DNI2lossResult = new MeanSquaredError().Evaluate(DNI2Result, layer3BackwardResult, out DNI2loss);
                    DNI2.Backward(DNI2lossResult);
                    DNI2.Update();
                    DNI2totalLoss += DNI2loss;
                    DNI2totalLossCount++;

                    //第四層を実行
                    BatchArray layer4ForwardResult = Layer4.Forward(layer3ForwardResult);

                    //第四層の傾きを取得
                    Real sumLoss;
                    BatchArray lossResult = new SoftmaxCrossEntropy().Evaluate(layer4ForwardResult, datasetX.Label, out sumLoss);

                    //第四層を更新
                    BatchArray layer4BackwardResult = Layer4.Backward(lossResult);
                    Layer4.Update();
                    totalLoss = sumLoss;
                    totalLossCount++;

                    //第三層用のDNIの学習を実行
                    Real DNI3loss;
                    BatchArray DNI3lossResult = new MeanSquaredError().Evaluate(DNI3Result, layer4BackwardResult, out DNI3loss);
                    DNI3.Backward(DNI3lossResult);
                    DNI3.Update();
                    DNI3totalLoss = DNI3loss;
                    DNI3totalLossCount++;

                    Console.WriteLine("\nbatch count " + i + "/" + TRAIN_DATA_COUNT);
                    //結果出力
                    Console.WriteLine("total loss " + totalLoss / totalLossCount);
                    Console.WriteLine("local loss " + sumLoss);

                    Console.WriteLine("\nDNI1 total loss " + DNI1totalLoss / DNI1totalLossCount);
                    Console.WriteLine("DNI2 total loss " + DNI2totalLoss / DNI2totalLossCount);
                    Console.WriteLine("DNI3 total loss " + DNI3totalLoss / DNI3totalLossCount);

                    Console.WriteLine("\nDNI1 local loss " + DNI1loss);
                    Console.WriteLine("DNI2 local loss " + DNI2loss);
                    Console.WriteLine("DNI3 local loss " + DNI3loss);

                    //20回バッチを動かしたら精度をテストする
                    if (i % 20 == 0)
                    {
                        Console.WriteLine("\nTesting...");

                        //テストデータからランダムにデータを取得
                        MnistDataSet datasetY = mnistData.GetRandomYSet(TEST_DATA_COUNT);

                        //テストを実行
                        Real accuracy = Trainer.Accuracy(nn, datasetY.Data, datasetY.Label);
                        Console.WriteLine("accuracy " + accuracy);
                    }
                }
            }
        }
    }
}
