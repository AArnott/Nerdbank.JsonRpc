// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

BenchmarkRunner.Run(
	typeof(Program).Assembly,
	DefaultConfig.Instance/*.WithOptions(ConfigOptions.DisableOptimizationsValidator)*/);
