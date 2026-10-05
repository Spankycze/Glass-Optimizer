using System;

namespace SpankyhoOptimizer.Services;

public static class AiFanBiasPolicy
{
    public static int Compute(
        double temp,
        double predictedTemp10s,
        double power,
        double load,
        double trend,
        double anomaly,
        string gamePhase,
        double confidence,
        AiWorkloadClass workload)
    {
        int thermalBias = 0;
        if (trend >= 1.0) thermalBias += 220;
        else if (trend >= 0.55) thermalBias += 140;
        else if (trend >= 0.25) thermalBias += 75;

        if (temp >= 78) thermalBias += 220;
        else if (temp >= 72) thermalBias += 130;
        else if (temp >= 68) thermalBias += 60;

        if (predictedTemp10s >= 70) thermalBias += 180;
        else if (predictedTemp10s >= 66) thermalBias += 100;
        else if (predictedTemp10s >= 63) thermalBias += 50;

        int adaptiveBias = 0;
        if (power >= 85) adaptiveBias += 140;
        else if (power >= 70) adaptiveBias += 80;

        int loadBias = load >= 70 ? 100 : load >= 45 ? 50 : 0;
        if (workload == AiWorkloadClass.Background)
            loadBias = (int)Math.Round(loadBias * 0.35);
        else if (workload == AiWorkloadClass.Idle)
            loadBias = 0;
        adaptiveBias += loadBias;

        if (anomaly >= 75) adaptiveBias += 180;
        else if (anomaly >= 50) adaptiveBias += 90;

        if (gamePhase == "Loading/Heavy") adaptiveBias += 100;
        else if (gamePhase == "Gameplay") adaptiveBias += 50;

        if (confidence < 45)
            adaptiveBias = (int)Math.Round(adaptiveBias * 0.45);
        else if (confidence < 70)
            adaptiveBias = (int)Math.Round(adaptiveBias * 0.75);

        int bias = thermalBias + adaptiveBias;

        if (temp <= 58 && predictedTemp10s <= 58.5 && trend <= -0.35 && anomaly < 30)
            bias -= 75;

        return Math.Clamp(bias, -100, 520);
    }
}
